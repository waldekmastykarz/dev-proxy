// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.ApiControllers;

sealed class JwtInfo
{
    public required string Token { get; set; }
}