﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.ExternalAccess.Razor;

namespace Microsoft.CodeAnalysis.Razor.Workspaces
{
    internal class RazorDocumentServiceProvider : IRazorDocumentServiceProvider, IRazorDocumentOperationService
    {
        private readonly DynamicDocumentContainer _documentContainer;
        private readonly object _lock;

        private IRazorSpanMappingService _spanMappingService;
        private IRazorDocumentExcerptService _excerptService;

        public RazorDocumentServiceProvider()
            : this(null)
        {
        }

        public RazorDocumentServiceProvider(DynamicDocumentContainer documentContainer)
        {
            _documentContainer = documentContainer;

            _lock = new object();
        }

        public bool CanApplyChange => false;

        public bool SupportDiagnostics => false;

        public TService GetService<TService>() where TService : class
        {
            if (_documentContainer == null)
            {
                return this as TService;
            }

            if (typeof(TService) == typeof(IRazorSpanMappingService))
            {
                if (_spanMappingService == null)
                {
                    lock (_lock)
                    {
                        if (_spanMappingService == null)
                        {
                            _spanMappingService = _documentContainer.GetMappingService();
                        }
                    }
                }

                return (TService)(object)_spanMappingService;
            }

            if (typeof(TService) == typeof(IRazorDocumentExcerptService))
            {
                if (_excerptService == null)
                {
                    lock (_lock)
                    {
                        if (_excerptService == null)
                        {
                            _excerptService = _documentContainer.GetExcerptService();
                        }
                    }
                }

                return (TService)(object)_excerptService;
            }

            return this as TService;
        }
    }
}
