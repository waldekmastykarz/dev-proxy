// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.Utils;
using Xunit;

namespace DevProxy.Integration.Tests;

public class GraphUtilsTests
{
    [Theory]
    [InlineData(
        "https://graph.microsoft.com/v1.0/drive/root:/Documents/report.xlsx:/workbook/worksheets",
        "/me/drive/items/{id}/workbook/worksheets")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/drives/drive-id/root:/Documents/report.xlsx",
        "/drives/{drives-id}/items/{id}")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/groups/group-id/drive/root:/Documents/report.xlsx:/content",
        "/groups/{groups-id}/drive/items/{id}/content")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/drive/root:/Documents/report.xlsx:/permissions",
        "/me/drive/items/{id}/permissions")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/sites/{site-id}/drive/root:/Documentos/informe.pdf:/content",
        "/sites/{site-id}/drive/items/{id}/content")]
    [InlineData(
        "https://graph.microsoft.com/beta/users/user-id/drive/root:/Documents/report.xlsx:/children",
        "/users/{users-id}/drive/items/{id}/children")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/drives/drive-id/items/parent-id:/Documents/report.xlsx:/createUploadSession",
        "/drives/{drives-id}/items/{id}/createUploadSession")]
    public void GetTokenizedUrl_RootRelativePath_ReturnsIdAddressedUrl(string url, string expected) =>
        Assert.Equal(expected, GraphUtils.GetTokenizedUrl(url));

    [Theory]
    [InlineData("My%20Files/quarterly-report%20%231.xlsx")]
    [InlineData("O'Brien/estimate%25s.xlsx")]
    [InlineData("Break%23Out/saved_game%5B1%5D.bin")]
    public void GetTokenizedUrl_PathWithSpecialCharacters_ReturnsIdAddressedUrl(string itemPath)
    {
        var url = $"https://graph.microsoft.com/v1.0/me/drive/root:/{itemPath}:/content";

        Assert.Equal("/me/drive/items/{id}/content", GraphUtils.GetTokenizedUrl(url));
    }

    [Theory]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/drive/items/parent-id:/Documents/report.txt:/content",
        "/me/drive/items/{id}/content")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/drive/special/documents:/report.txt:/content",
        "/me/drive/items/{id}/content")]
    public void GetTokenizedUrl_ItemRelativePath_ReturnsIdAddressedUrl(string url, string expected) =>
        Assert.Equal(expected, GraphUtils.GetTokenizedUrl(url));

    [Fact]
    public void GetTokenizedUrl_NonPathAddressedUrl_PreservesExistingTokenization() =>
        Assert.Equal(
            "/sites/{sites-id}/drive/items/{items-id}/content",
            GraphUtils.GetTokenizedUrl("https://graph.microsoft.com/v1.0/sites/site-id/drive/items/item-id/content"));

    [Theory]
    [InlineData(
        "https://graph.microsoft.com/v1.0/users('48d31887-5fad-4d73-a9f5-3c356e68a038')",
        "/users(<value>)")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/users('john@contoso.com')/messages",
        "/users(<value>)/messages")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/applications(appId='46e6adf4-a9cf-4b60-9390-0ba6fb00bf6b')",
        "/applications(appId=<value>)")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/oauth2PermissionGrants('grant-id')",
        "/oauth2PermissionGrants(<value>)")]
    public void GetTokenizedUrl_KeyInParentheses_ReturnsTokenizedKey(string url, string expected) =>
        Assert.Equal(expected, GraphUtils.GetTokenizedUrl(url));

    [Theory]
    [InlineData(
        "https://graph.microsoft.com/v1.0/reports/getOffice365ActiveUserDetail(period='D7')",
        "/reports/getOffice365ActiveUserDetail(period=<value>)")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/calendarView/delta()",
        "/me/calendarView/delta()")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/drive/root/search(q='budget, 2024=final')",
        "/me/drive/root/search(q=<value>)")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/drive/root/search(q='O''Brien')",
        "/me/drive/root/search(q=<value>)")]
    [InlineData(
        "https://graph.microsoft.com/v1.0/me/drive/items/1/getActivitiesByInterval(startDateTime='2017-01-01',endDateTime='2017-01-03',interval='day')",
        "/me/drive/items/{items-id}/getActivitiesByInterval(startDateTime=<value>,endDateTime=<value>,interval=<value>)")]
    [InlineData(
        "https://graph.microsoft.com/beta/reports/microsoft.graph.getTeamsUserActivityUserDetail(date=2024-01-01)",
        "/reports/microsoft.graph.getTeamsUserActivityUserDetail(date=<value>)")]
    public void GetTokenizedUrl_FunctionWithParameters_ReturnsTokenizedParameters(string url, string expected) =>
        Assert.Equal(expected, GraphUtils.GetTokenizedUrl(url));

    [Fact]
    public void GetTokenizedUrl_RepeatedSegmentValues_TokenizesEachSegmentIndependently() =>
        Assert.Equal(
            "/sites/root/lists/{lists-id}/items/{items-id}",
            GraphUtils.GetTokenizedUrl("https://graph.microsoft.com/v1.0/sites/root/lists/1/items/1"));

    [Fact]
    public void GetRequestsFromBatch_PathAddressedRequest_ReturnsIdAddressedUrl()
    {
        const string batchBody =
            """
            {
              "requests": [
                {
                  "id": "1",
                  "method": "PUT",
                  "url": "/sites/site-id/drive/root:/Documents/report.txt:/content"
                },
                {
                  "id": "2",
                  "method": "GET",
                  "url": "/sites/site-id/drive/items/item-id"
                }
              ]
            }
            """;

        var requests = GraphUtils.GetRequestsFromBatch(batchBody, "v1.0", "graph.microsoft.com");

        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal("PUT", request.Method);
                Assert.Equal("/sites/{sites-id}/drive/items/{id}/content", request.Url);
            },
            request =>
            {
                Assert.Equal("GET", request.Method);
                Assert.Equal("/sites/{sites-id}/drive/items/{items-id}", request.Url);
            });
    }
}
