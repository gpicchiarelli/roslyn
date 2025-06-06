﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol;

/// <summary>
/// Capabilities specific to the `workspace/executeCommand` request.
/// <para>
/// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#executeCommandClientCapabilities">Language Server Protocol specification</see> for additional information.
/// </para>
/// </summary>
internal sealed class ExecuteCommandClientCapabilities : DynamicRegistrationSetting
{
}
