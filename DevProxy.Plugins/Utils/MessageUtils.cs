﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.Utils;

internal sealed class MessageUtils
{
    public static string BuildUseSdkForErrorsMessage() =>
        $"To handle API errors more easily, use the Microsoft Graph SDK. More info at {GetMoveToSdkUrl()}";

    public static string BuildUseSdkMessage() =>
        $"To more easily follow best practices for working with Microsoft Graph, use the Microsoft Graph SDK. More info at {GetMoveToSdkUrl()}";

    public static string GetMoveToSdkUrl()
    {
        // TODO: return language-specific guidance links based on the language detected from the User-Agent
        return "https://aka.ms/devproxy/guidance/move-to-js-sdk";
    }
}
