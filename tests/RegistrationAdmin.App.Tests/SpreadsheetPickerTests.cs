using System.IO;
using System.Net;
using RegistrationAdmin.GoogleSheets.Auth;

namespace RegistrationAdmin.App.Tests;

public sealed class SpreadsheetPickerTests
{
    [Fact]
    public void Authorization_UsesSingleFileScopeAndPkce_AndFiltersSpreadsheets()
    {
        var url = GoogleSpreadsheetPicker.BuildAuthorizationUrl("client", "person@example.test", "http://127.0.0.1:1234/picker/", "random-state", "verifier");
        var query = PickerCallbackListener.ParseQuery(new Uri(url).Query.TrimStart('?'));
        Assert.Equal("https://accounts.google.com", new Uri(url).GetLeftPart(UriPartial.Authority));
        Assert.Equal(GoogleSpreadsheetPicker.Scope, query["scope"]);
        Assert.Equal("false", query["include_granted_scopes"]);
        Assert.Equal(GoogleSpreadsheetPicker.SpreadsheetMimeType, query["mimetypes"]);
        Assert.Equal("false", query["allow_multiple"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.NotEqual("verifier", query["code_challenge"]);
        Assert.Equal("random-state", query["state"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("true", query["trigger_onepick"]);
    }

    [Fact]
    public async Task Callback_RejectsOldState_ThenAcceptsValidSelection()
    {
        using var listener = new PickerCallbackListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = listener.ReceiveAsync("current-state", timeout.Token);
        using var browser = new HttpClient();
        using var stale = await browser.GetAsync(listener.RedirectUri + "?state=old&code=code&picked_file_ids=sheet", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.False(receive.IsCompleted);
        using var valid = await browser.GetAsync(listener.RedirectUri + "?state=current-state&code=code&picked_file_ids=sheet", timeout.Token);
        var selected = await receive;
        Assert.Equal("sheet", selected!.FileId);
        Assert.Equal("code", selected.Code);
    }

    [Fact]
    public async Task Callback_CancelWait_AllowsImmediateNewAttempt()
    {
        using (var previous = new PickerCallbackListener())
        {
            using var cancel = new CancellationTokenSource();
            var receive = previous.ReceiveAsync("old", cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        }
        using var next = new PickerCallbackListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var retry = next.ReceiveAsync("new", timeout.Token);
        using var browser = new HttpClient();
        using var response = await browser.GetAsync(next.RedirectUri + "?state=new&error=access_denied", timeout.Token);
        Assert.Null(await retry);
    }

    [Theory]
    [InlineData("state=x&code=code&picked_file_ids=a%2Cb")]
    [InlineData("state=x&code=code&picked_file_ids=../other")]
    [InlineData("state=x&picked_file_ids=sheet")]
    [InlineData("state=x&code=code&picked_file_ids=sheet&picked_file_ids=other")]
    public void Callback_RejectsMalformedOrMultipleSelections(string query) =>
        Assert.Throws<InvalidOperationException>(() => PickerCallbackListener.ParseResult(PickerCallbackListener.ParseQuery(query)));

    [Theory]
    [InlineData("fixture@example.test", true, "application/vnd.google-apps.spreadsheet", true)]
    [InlineData("other@example.test", true, "application/vnd.google-apps.spreadsheet", false)]
    [InlineData("fixture@example.test", false, "application/vnd.google-apps.spreadsheet", false)]
    [InlineData("fixture@example.test", true, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", false)]
    public async Task Picker_VerifiesAccountAndEditPermissionBeforeReturningFile(string selectedAccount, bool canEdit, string mimeType, bool succeeds)
    {
        var secretPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(secretPath, "{\"installed\":{\"client_id\":\"fixture-client\",\"client_secret\":\"fixture-secret\"}}");
        try
        {
            Task<HttpResponseMessage>? browserTask = null;
            using var browser = new HttpClient();
            var network = new FakeGoogleHandler(selectedAccount, canEdit, mimeType);
            var picker = new GoogleSpreadsheetPicker(new PickerSettings(secretPath), url =>
            {
                var query = PickerCallbackListener.ParseQuery(new Uri(url).Query.TrimStart('?'));
                browserTask = browser.GetAsync(query["redirect_uri"] + "?state=" + query["state"] + "&code=fixture-code&picked_file_ids=fixture-sheet");
            }, () => new HttpClient(network));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (succeeds)
            {
                var result = await picker.PickAsync("fixture@example.test", timeout.Token);
                Assert.Equal(new PickedSpreadsheet("fixture-sheet", "Fixture spreadsheet"), result);
            }
            else await Assert.ThrowsAsync<InvalidOperationException>(() => picker.PickAsync("fixture@example.test", timeout.Token));
            Assert.NotNull(browserTask);
            using var callback = await browserTask;
            Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
            Assert.True(network.ExchangedCode);
        }
        finally { File.Delete(secretPath); }
    }

    private sealed record PickerSettings(string ClientSecretPath) : IConnectionSettings
    {
        public string SpreadsheetId => "";
        public string TokenDirectory => "";
    }

    private sealed class FakeGoogleHandler(string account, bool canEdit, string mimeType) : HttpMessageHandler
    {
        public bool ExchangedCode { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var form = PickerCallbackListener.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("fixture-code", form["code"]);
                Assert.True(form["code_verifier"].Length >= 43);
                ExchangedCode = true;
                json = "{\"access_token\":\"fixture-access\"}";
            }
            else
            {
                Assert.Equal("www.googleapis.com", request.RequestUri.Host);
                Assert.Equal("fixture-access", request.Headers.Authorization!.Parameter);
                json = request.RequestUri.AbsolutePath.EndsWith("/about")
                    ? System.Text.Json.JsonSerializer.Serialize(new { user = new { emailAddress = account } })
                    : System.Text.Json.JsonSerializer.Serialize(new { name = "Fixture spreadsheet", mimeType, trashed = false, capabilities = new { canEdit } });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
