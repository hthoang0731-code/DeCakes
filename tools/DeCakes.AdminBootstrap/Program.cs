using DeCakes.AdminBootstrap;
using DeCakes.Application.Admin;
using DeCakes.Infrastructure.Accounts;
using DeCakes.Infrastructure.Admin;
using DeCakes.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

// No CLI configuration provider or development user-secrets. Credentials never enter arguments or logs.
return await BootstrapConsole.RunAsync(args, new OperatorPrompt(), () =>
{
    var raw = Environment.GetEnvironmentVariable("DECAKES_ADMIN_BOOTSTRAP_CONNECTION");
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DeCakesDb"] = raw,
    }).Build();
    return new AdminBootstrapService(new MySqlAdminProvisioningStore(new MySqlConnectionFactory(configuration)), new AspNetPasswordHashService());
});
