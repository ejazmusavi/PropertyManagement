using System.Net;
using RealEstate.Tests.Support;

namespace RealEstate.Web.Tests;

[Collection("web")]
public class WebTests(WebFactory f)
{
    private static FormUrlEncodedContent Form(params (string, string)[] kv) =>
        new(kv.ToDictionary(x => x.Item1, x => x.Item2));

    [MySqlFact]
    public async Task Anonymous_IsRedirectedToLogin()
    {
        var r = await f.NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
    }

    [MySqlFact]
    public async Task Anonymous_CannotOpenUsersOrProfile()
    {
        var c = f.NewClient();
        foreach (var url in new[] { "/Users", "/Profile", "/Profile/ChangePassword" })
            Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync(url)).StatusCode);
    }

    [MySqlFact]
    public async Task LoginPage_Renders()
    {
        var html = await f.NewClient().GetStringAsync("/Account/Login");
        Assert.Contains("Sign in", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [MySqlFact]
    public async Task Login_Valid_ShowsDashboardAndNavigation()
    {
        var c = await f.LoggedInAsync(administrator: true);
        var html = await c.GetStringAsync("/");
        Assert.Contains("Welcome, Web Tester", html);
        Assert.Contains("Active users", html);
        Assert.Contains(">Users<", html);
    }

    [MySqlFact]
    public async Task Login_Invalid_ShowsGenericError()
    {
        var c = f.NewClient();
        var r = await c.PostAsync("/Account/Login", Form(("Email", "nobody@example.com"), ("Password", "WrongPass#1234"),
            ("__RequestVerificationToken", await WebFactory.TokenAsync(c, "/Account/Login"))));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Invalid email or password.", await r.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task Login_WithoutAntiForgeryToken_IsRejected()
    {
        var r = await f.NewClient().PostAsync("/Account/Login", Form(("Email", "a@b.com"), ("Password", "x")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [MySqlFact]
    public async Task Login_ExternalReturnUrl_IsIgnored()
    {
        var email = await f.CreateUserAsync(true);
        var c = f.NewClient();
        var r = await c.PostAsync("/Account/Login", Form(("Email", email), ("Password", WebFactory.Password), ("ReturnUrl", "https://evil.example/"),
            ("__RequestVerificationToken", await WebFactory.TokenAsync(c, "/Account/Login"))));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.DoesNotContain("evil.example", r.Headers.Location!.ToString());
    }

    [MySqlFact]
    public async Task Users_Admin_SeesListWithSearch()
    {
        var c = await f.LoggedInAsync(true);
        var html = await c.GetStringAsync("/Users");
        Assert.Contains("Web Tester", html);
        var none = await c.GetStringAsync("/Users?search=zzz-no-such-user");
        Assert.Contains("No users match your search.", none);
    }

    [MySqlFact]
    public async Task Users_WithoutPermission_IsDeniedAndHiddenFromNav()
    {
        var c = await f.LoggedInAsync(administrator: false);
        var r = await c.GetAsync("/Users");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
        Assert.DoesNotContain(">Users<", await c.GetStringAsync("/"));
    }

    [MySqlFact]
    public async Task Profile_Update_PersistsAndValidates()
    {
        var c = await f.LoggedInAsync(false);
        var token = await WebFactory.TokenAsync(c, "/Profile");
        var ok = await c.PostAsync("/Profile", Form(("FullName", "Renamed Person"), ("PhoneNumber", "0300-1234567"), ("__RequestVerificationToken", token)));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("Renamed Person", await c.GetStringAsync("/Profile"));

        var bad = await c.PostAsync("/Profile", Form(("FullName", ""), ("PhoneNumber", "abc"), ("__RequestVerificationToken", await WebFactory.TokenAsync(c, "/Profile"))));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("field-validation-error", await bad.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task ChangePassword_WrongCurrent_ShowsError_ThenSucceeds()
    {
        var c = await f.LoggedInAsync(false);
        const string next = "N3w#Passw0rd!!x";
        var wrong = await c.PostAsync("/Profile/ChangePassword", Form(("CurrentPassword", "Wrong#Passw0rd!!"), ("NewPassword", next), ("ConfirmNewPassword", next),
            ("__RequestVerificationToken", await WebFactory.TokenAsync(c, "/Profile/ChangePassword"))));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Current password is incorrect.", await wrong.Content.ReadAsStringAsync());

        var ok = await c.PostAsync("/Profile/ChangePassword", Form(("CurrentPassword", WebFactory.Password), ("NewPassword", next), ("ConfirmNewPassword", next),
            ("__RequestVerificationToken", await WebFactory.TokenAsync(c, "/Profile/ChangePassword"))));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [MySqlFact]
    public async Task Logout_EndsSession()
    {
        var c = await f.LoggedInAsync(true);
        var r = await c.PostAsync("/Account/Logout", Form(("__RequestVerificationToken", await WebFactory.TokenAsync(c, "/Profile"))));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode);
    }

    [MySqlFact]
    public async Task UnknownPage_ShowsFriendly404()
    {
        var c = await f.LoggedInAsync(true);
        var r = await c.GetAsync("/does-not-exist");
        Assert.Contains("Page not found", await r.Content.ReadAsStringAsync());
    }
}
