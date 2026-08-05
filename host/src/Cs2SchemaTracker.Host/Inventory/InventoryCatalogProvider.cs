// IOptions<InventoryCatalog> provider.
//
// The host is a CLI with no DI container, so this static factory wraps a loaded
// InventoryCatalog in Microsoft.Extensions.Options.Options.Create — giving call sites the
// IOptions<T> abstraction the user asked for without a service collection. The inventory path is
// resolved with the same precedence HostConfig uses: an appsettings InventoryPath (when set),
// else the repo-root-relative default (data/cs2-assets-inventory.json).

using Microsoft.Extensions.Options;

namespace Cs2SchemaTracker.Host.Inventory;

/// <summary>Produces an <see cref="IOptions{InventoryCatalog}"/> from the inventory file.</summary>
internal static class InventoryCatalogProvider
{
    /// <summary>Env var that overrides the inventory path (mirrors CS2_WALKER_ERAS_ROOT etc.).</summary>
    public const string InventoryPathEnvVar = "CS2_INVENTORY_PATH";


    /// <summary>
    /// Load the catalog at <paramref name="inventoryPath"/> and wrap it as
    /// <see cref="IOptions{InventoryCatalog}"/>. Fail-loud (see <see cref="InventoryCatalog.LoadFromFile"/>).
    /// </summary>
    public static IOptions<InventoryCatalog> Load(string inventoryPath)
        => Options.Create(InventoryCatalog.LoadFromFile(inventoryPath));

    /// <summary>
    /// Resolve the effective inventory path for <paramref name="repoRoot"/>, in precedence order:
    /// <list type="number">
    ///   <item>the appsettings <c>InventoryPath</c> (env-overridable via HostConfig) when set — a
    ///     RELATIVE value resolves against the app dir (<c>AppContext.BaseDirectory</c>), NOT the cwd;
    ///     an absolute value is honored as-is;</item>
    ///   <item><c>&lt;repoRoot&gt;/data/cs2-assets-inventory.json</c> when it exists — the LIVE
    ///     single-source inventory during an in-repo run;</item>
    ///   <item><c>&lt;AppContext.BaseDirectory&gt;/data/cs2-assets-inventory.json</c> — the copy
    ///     SHIPPED next to the host (the csproj Content copy), so the app runs with NO repo present.</item>
    /// </list>
    /// The shipped fallback is only reached when the repo copy is absent, so it never shadows an
    /// in-repo run. When nothing exists, the repo-root path is returned so the fail-loud
    /// <see cref="InventoryCatalog.LoadFromFile"/> error names the primary location.
    /// </summary>
    public static string ResolveInventoryPath(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        var configured = Cs2SchemaTracker.Host.Config.HostConfig.InventoryPath;
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
        }

        var repoInventory = Path.Combine(repoRoot, "data", "cs2-assets-inventory.json");
        if (File.Exists(repoInventory))
        {
            return repoInventory;
        }

        // Standalone/bundle: fall back to the inventory shipped next to the host. Never shadows an
        // in-repo run (that resolves at repoInventory above).
        var shipped = Path.Combine(AppContext.BaseDirectory, "data", "cs2-assets-inventory.json");
        if (File.Exists(shipped))
        {
            return shipped;
        }

        return repoInventory;   // nothing found — name the primary location in the fail-loud error.
    }
}
