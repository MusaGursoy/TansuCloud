// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.ComponentModel.DataAnnotations;

namespace TansuCloud.Database.Provisioning;

public sealed class ProvisioningOptions
{
    [Required]
    public string AdminConnectionString { get; set; } = string.Empty; // connects to 'postgres' database

    public string DatabaseNamePrefix { get; set; } = "tansu_tenant_";

    // Comma-separated list of extensions to ensure in tenant DB
    public string Extensions { get; set; } = "citus,vector";
} // End of Class ProvisioningOptions
