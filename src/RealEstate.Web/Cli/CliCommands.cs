using Microsoft.Extensions.DependencyInjection;
using RealEstate.Infrastructure.Persistence;
using RealEstate.Infrastructure.Persistence.Bootstrap;

namespace RealEstate.Web.Cli;

/// <summary>
/// Operational commands run instead of the web server:
///   migrate
///   create-admin --email you@example.com --name "Full Name" --company "Company Name"
/// The administrator password is read from the REALESTATE_ADMIN_PASSWORD environment variable or prompted
/// (hidden input). It is never accepted as a command-line argument and never written to a file.
/// </summary>
public static class CliCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "migrate" or "create-admin";

    public static async Task<int> RunAsync(IServiceProvider root, string[] args)
    {
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;
        switch (args[0])
        {
            case "migrate":
                await sp.GetRequiredService<DatabaseInitializer>().MigrateAsync();
                Console.WriteLine("Database is up to date.");
                return 0;

            case "create-admin":
                var opts = Parse(args.Skip(1).ToArray());
                if (!opts.TryGetValue("email", out var email) || !opts.TryGetValue("name", out var name))
                {
                    Console.Error.WriteLine("Usage: create-admin --email <email> --name <full name> [--company <company name>]");
                    return 2;
                }
                var password = Environment.GetEnvironmentVariable("REALESTATE_ADMIN_PASSWORD") ?? PromptPassword();
                if (string.IsNullOrEmpty(password))
                {
                    Console.Error.WriteLine("No password supplied. Set REALESTATE_ADMIN_PASSWORD or run interactively.");
                    return 2;
                }
                var company = opts.GetValueOrDefault("company") ?? "My Company";
                var result = await sp.GetRequiredService<AdminBootstrapper>().CreateInitialAdministratorAsync(email, name, password, company);
                if (!result.Succeeded)
                {
                    Console.Error.WriteLine(result.Error!.Message);
                    return 1;
                }
                Console.WriteLine($"Administrator '{email}' created.");
                return 0;
        }
        return 2;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
            if (args[i].StartsWith("--")) d[args[i][2..]] = args[i + 1];
        return d;
    }

    private static string? PromptPassword()
    {
        if (Console.IsInputRedirected) return null;
        string Read(string prompt)
        {
            Console.Write(prompt);
            var sb = new System.Text.StringBuilder();
            ConsoleKeyInfo k;
            while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
                else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString();
        }
        var p1 = Read("Administrator password: ");
        var p2 = Read("Confirm password: ");
        if (p1 != p2) { Console.Error.WriteLine("Passwords do not match."); return null; }
        return p1;
    }
}
