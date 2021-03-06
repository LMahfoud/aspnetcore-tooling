﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using MediatR;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Microsoft.AspNetCore.Razor.LanguageServer.Semantic
{
    [Method(LanguageServerConstants.RazorSemanticTokenLegendEndpoint)]
    internal interface ISemanticTokenLegendHandler :
        IJsonRpcRequestHandler<SemanticTokenLegendParams, SemanticTokenLegendResponse>,
        IRequestHandler<SemanticTokenLegendParams, SemanticTokenLegendResponse>,
        IJsonRpcHandler,
        ICapability<SemanticTokenLegendCapability>
    {
    }
}
