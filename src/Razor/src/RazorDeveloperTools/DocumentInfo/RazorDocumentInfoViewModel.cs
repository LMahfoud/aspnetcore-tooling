﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Editor.Razor;

namespace Microsoft.VisualStudio.RazorExtension.DocumentInfo
{
    public class RazorDocumentInfoViewModel : NotifyPropertyChanged
    {
        private readonly VisualStudioDocumentTracker _documentTracker;

        public RazorDocumentInfoViewModel(VisualStudioDocumentTracker documentTracker)
        {
            if (documentTracker == null)
            {
                throw new ArgumentNullException(nameof(documentTracker));
            }

            _documentTracker = documentTracker;
        }

        public string Configuration => _documentTracker.Configuration?.ConfigurationName;

        public bool IsSupportedDocument => _documentTracker.IsSupportedProject;

        public Workspace Workspace => _documentTracker.Workspace;
    }
}