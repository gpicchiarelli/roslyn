﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.DiagnosticSources;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.Public;

// A document diagnostic partial report is defined as having the first literal send = DocumentDiagnosticReport (aka the sumtype of changed / unchanged) followed
// by n DocumentDiagnosticPartialResult literals.
// See https://github.com/microsoft/vscode-languageserver-node/blob/main/protocol/src/common/proposed.diagnostics.md#textDocument_diagnostic
[ExportCSharpVisualBasicLspServiceFactory(typeof(PublicDocumentPullDiagnosticsHandler)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class PublicDocumentPullDiagnosticHandlerFactory(
    IDiagnosticSourceManager diagnosticSourceManager,
    IDiagnosticsRefresher diagnosticRefresher,
    IGlobalOptionService globalOptions) : ILspServiceFactory
{
    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind)
    {
        var clientLanguageServerManager = lspServices.GetRequiredService<IClientLanguageServerManager>();
        return new PublicDocumentPullDiagnosticsHandler(clientLanguageServerManager, diagnosticSourceManager, diagnosticRefresher, globalOptions);
    }
}
