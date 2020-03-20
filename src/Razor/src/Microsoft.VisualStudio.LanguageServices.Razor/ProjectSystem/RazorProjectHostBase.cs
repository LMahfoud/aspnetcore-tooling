// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.LanguageServices.Razor.Serialization;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem
{
    internal abstract class RazorProjectHostBase : OnceInitializedOnceDisposedAsync, IProjectDynamicLoadComponent
    {
        private readonly Workspace _workspace;
        private readonly AsyncSemaphore _lock;

        internal readonly Dictionary<string, Task> _deferredPublishTasks;
        private readonly Dictionary<string, ProjectSnapshot> _pendingProjectPublishes;
        private readonly object _publishLock;
        private readonly Dictionary<string, string> _publishFilePathMappings;


        private ProjectSnapshotManagerBase _projectManager;
        private readonly Dictionary<string, HostDocument> _currentDocuments;
        private readonly JsonSerializer _serializer = new JsonSerializer()
        {
            Formatting = Newtonsoft.Json.Formatting.Indented
        };

        // Internal settable for testing
        // 250ms between publishes to prevent bursts of changes yet still be responsive to changes.
        internal int EnqueueDelay { get; set; } = 250;

        public RazorProjectHostBase(
            IUnconfiguredProjectCommonServices commonServices,
            [Import(typeof(VisualStudioWorkspace))] Workspace workspace)
            : base(commonServices.ThreadingService.JoinableTaskContext)
        {
            if (commonServices == null)
            {
                throw new ArgumentNullException(nameof(commonServices));
            }

            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            CommonServices = commonServices;
            _workspace = workspace;

            _serializer.Converters.Add(TagHelperDescriptorJsonConverter.Instance);
            _serializer.Converters.Add(RazorConfigurationJsonConverter.Instance);
            _serializer.Converters.Add(AspNetCore.Razor.LanguageServer.Common.Serialization.ProjectSnapshotJsonConverter.Instance);

            _publishFilePathMappings = new Dictionary<string, string>(FilePathComparer.Instance);
            _deferredPublishTasks = new Dictionary<string, Task>(FilePathComparer.Instance);
            _pendingProjectPublishes = new Dictionary<string, ProjectSnapshot>(FilePathComparer.Instance);
            _publishLock = new object();

            _lock = new AsyncSemaphore(initialCount: 1);
            _currentDocuments = new Dictionary<string, HostDocument>(FilePathComparer.Instance);
        }

        // Internal for testing
        protected RazorProjectHostBase(
            IUnconfiguredProjectCommonServices commonServices,
             Workspace workspace,
             ProjectSnapshotManagerBase projectManager)
            : base(commonServices.ThreadingService.JoinableTaskContext)
        {
            if (commonServices == null)
            {
                throw new ArgumentNullException(nameof(commonServices));
            }

            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            if (projectManager == null)
            {
                throw new ArgumentNullException(nameof(projectManager));
            }

            CommonServices = commonServices;
            _workspace = workspace;
            _projectManager = projectManager;

            _lock = new AsyncSemaphore(initialCount: 1);
            _currentDocuments = new Dictionary<string, HostDocument>(FilePathComparer.Instance);
        }

        protected HostProject Current { get; private set; }

        protected IUnconfiguredProjectCommonServices CommonServices { get; }

        // internal for tests. The product will call through the IProjectDynamicLoadComponent interface.
        internal Task LoadAsync()
        {
            return InitializeAsync();
        }

        protected override Task InitializeCoreAsync(CancellationToken cancellationToken)
        {
            CommonServices.UnconfiguredProject.ProjectRenaming += UnconfiguredProject_ProjectRenaming;

            return Task.CompletedTask;
        }

        protected override async Task DisposeCoreAsync(bool initialized)
        {
            if (initialized)
            {
                CommonServices.UnconfiguredProject.ProjectRenaming -= UnconfiguredProject_ProjectRenaming;

                await ExecuteWithLock(async () =>
                {
                    if (Current != null)
                    {
                        await UpdateAsync(UninitializeProjectUnsafe).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }
        }

        public virtual void SetPublishFilePath(string projectFilePath, string publishFilePath)
        {
            lock (_publishLock)
            {
                _publishFilePathMappings[projectFilePath] = publishFilePath;
            }
        }

        // Internal for tests
        internal async Task OnProjectRenamingAsync()
        {
            // When a project gets renamed we expect any rules watched by the derived class to fire.
            //
            // However, the project snapshot manager uses the project Fullpath as the key. We want to just
            // reinitialize the HostProject with the same configuration and settings here, but the updated
            // FilePath.
            await ExecuteWithLock(async () =>
            {
                if (Current != null)
                {
                    var old = Current;
                    var oldDocuments = _currentDocuments.Values.ToArray();

                    await UpdateAsync(UninitializeProjectUnsafe).ConfigureAwait(false);

                    await UpdateAsync(() =>
                    {
                        var filePath = CommonServices.UnconfiguredProject.FullPath;
                        UpdateProjectUnsafe(new HostProject(filePath, old.Configuration, old.RootNamespace));

                        // This should no-op in the common case, just putting it here for insurance.
                        for (var i = 0; i < oldDocuments.Length; i++)
                        {
                            AddDocumentUnsafe(oldDocuments[i]);
                        }
                    }).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        // Should only be called from the UI thread.
        private ProjectSnapshotManagerBase GetProjectManager()
        {
            CommonServices.ThreadingService.VerifyOnUIThread();

            if (_projectManager == null)
            {
                _projectManager = (ProjectSnapshotManagerBase)_workspace.Services.GetLanguageServices(RazorLanguage.Name).GetRequiredService<ProjectSnapshotManager>();
                _projectManager.Changed += ProjectManager_Changed;
            }

            return _projectManager;
        }

        protected async Task UpdateAsync(Action action)
        {
            await CommonServices.ThreadingService.SwitchToUIThread();
            action();
        }

        protected void UninitializeProjectUnsafe()
        {
            ClearDocumentsUnsafe();
            UpdateProjectUnsafe(null);
        }

        protected void UpdateProjectUnsafe(HostProject project)
        {
            var projectManager = GetProjectManager();
            if (Current == null && project == null)
            {
                // This is a no-op. This project isn't using Razor.
            }
            else if (Current == null && project != null)
            {
                projectManager.ProjectAdded(project);
            }
            else if (Current != null && project == null)
            {
                Debug.Assert(_currentDocuments.Count == 0);
                projectManager.ProjectRemoved(Current);
            }
            else
            {
                projectManager.ProjectConfigurationChanged(project);
            }

            Current = project;
        }

        protected void AddDocumentUnsafe(HostDocument document)
        {
            var projectManager = GetProjectManager();

            if (_currentDocuments.ContainsKey(document.FilePath))
            {
                // Ignore duplicates
                return;
            }

            projectManager.DocumentAdded(Current, document, new FileTextLoader(document.FilePath, null));
            _currentDocuments.Add(document.FilePath, document);
        }

        protected void RemoveDocumentUnsafe(HostDocument document)
        {
            var projectManager = GetProjectManager();

            projectManager.DocumentRemoved(Current, document);
            _currentDocuments.Remove(document.FilePath);
        }

        protected void ClearDocumentsUnsafe()
        {
            var projectManager = GetProjectManager();

            foreach (var kvp in _currentDocuments)
            {
                projectManager.DocumentRemoved(Current, kvp.Value);
            }

            _currentDocuments.Clear();
        }
        
        protected async Task ExecuteWithLock(Func<Task> func)
        {
            using (JoinableCollection.Join())
            {
                using (await _lock.EnterAsync().ConfigureAwait(false))
                {
                    var task = JoinableFactory.RunAsync(func);
                    await task.Task.ConfigureAwait(false);
                }
            }
        }

        Task IProjectDynamicLoadComponent.LoadAsync()
        {
            return InitializeAsync();
        }

        Task IProjectDynamicLoadComponent.UnloadAsync()
        {
            return DisposeAsync();
        }

        private void ProjectManager_Changed(object sender, ProjectChangeEventArgs args)
        {
            switch (args.Kind)
            {
                case ProjectChangeKind.DocumentRemoved:
                case ProjectChangeKind.DocumentAdded:
                case ProjectChangeKind.DocumentChanged:
                case ProjectChangeKind.ProjectChanged:
                    // These changes can come in bursts so we don't want to overload the publishing system. Therefore,
                    // we enqueue publishes and then publish the latest project after a delay.

                    EnqueuePublish(args.Newer);
                    break;
                case ProjectChangeKind.ProjectAdded:
                    Publish(args.Newer);
                    break;
                case ProjectChangeKind.ProjectRemoved:
                    RemovePublishing(args.Older);
                    break;
            }
        }

        internal void EnqueuePublish(ProjectSnapshot projectSnapshot)
        {
            // A race is not possible here because we use the main thread to synchronize the updates
            // by capturing the sync context.

            CommonServices.ThreadingService.VerifyOnUIThread();

            _pendingProjectPublishes[projectSnapshot.FilePath] = projectSnapshot;

            if (!_deferredPublishTasks.TryGetValue(projectSnapshot.FilePath, out var update) || update.IsCompleted)
            {
                _deferredPublishTasks[projectSnapshot.FilePath] = PublishAfterDelay(projectSnapshot.FilePath);
            }
        }

        private async Task PublishAfterDelay(string projectFilePath)
        {
            await Task.Delay(EnqueueDelay);

            if (!_pendingProjectPublishes.TryGetValue(projectFilePath, out var projectSnapshot))
            {
                // Project was removed while waiting for the publish delay.
                return;
            }

            _pendingProjectPublishes.Remove(projectFilePath);

            Publish(projectSnapshot);
        }

        internal void Publish(ProjectSnapshot projectSnapshot)
        {
            if (projectSnapshot is null)
            {
                throw new ArgumentNullException(nameof(projectSnapshot));
            }

            lock(_publishLock)
            {
                string publishFilePath = null;
                try
                {
                    if(!_publishFilePathMappings.TryGetValue(projectSnapshot.FilePath, out publishFilePath))
                    {
                        return;
                    }

                    SerializeToFile(projectSnapshot, publishFilePath);
                }
                catch
                {
                    // TODO should log
                }
            }
        }

        internal void RemovePublishing(ProjectSnapshot projectSnapshot)
        {
            lock (_publishLock)
            {
                var oldProjectFilePath = projectSnapshot.FilePath;
                if (_publishFilePathMappings.TryGetValue(oldProjectFilePath, out var publishFilePath))
                {
                    if (_pendingProjectPublishes.TryGetValue(oldProjectFilePath, out _))
                    {
                        // Project was removed while a delayed publish was in flight. Clear the in-flight publish so it noops.
                        _pendingProjectPublishes.Remove(oldProjectFilePath);
                    }

                    DeleteFile(publishFilePath);
                }
            }
        }

        // Virtual for testing
        protected virtual void DeleteFile(string publishFilePath)
        {
            var info = new FileInfo(publishFilePath);
            if (info.Exists)
            {
                try
                {
                    // Try catch around the delete in case it was deleted between the Exists and this delete call. This also
                    // protects against unauthorized access issues.
                    info.Delete();
                }
                catch
                {
                    // TODO: logging here, but have to plumb it everywhere
                }
            }
        }

        protected virtual void SerializeToFile(ProjectSnapshot projectSnapshot, string publishFilePath)
        {
            CommonServices.ThreadingService.SwitchToUIThread().GetAwaiter().OnCompleted(() => {
                var fileInfo = new FileInfo(publishFilePath);
                using var writer = fileInfo.CreateText();
                _serializer.Serialize(writer, projectSnapshot);
            });
        }

        private async Task UnconfiguredProject_ProjectRenaming(object sender, ProjectRenamedEventArgs args)
        {
            await OnProjectRenamingAsync().ConfigureAwait(false);
        }
    }
}