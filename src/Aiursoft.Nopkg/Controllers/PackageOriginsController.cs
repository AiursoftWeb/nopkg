using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Aiursoft.Nopkg.Registry;

namespace Aiursoft.Nopkg.Controllers;

[AllowAnonymous, Route("api/package-origins")]
public class PackageOriginsController(ILocalPackageStore store, NpmRegistry registry) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string name, string version, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version) || version.Length > 128) return BadRequest();
        try { NpmRegistry.PackageName(name); }
        catch (ArgumentException) { return BadRequest(); }
        var local = await store.FindAsync(name, token);
        string? hash = null;
        string? archive = null;
        if (local != null && local.ArchiveHashes.TryGetValue(version, out var expected))
        {
            await using var stream = System.IO.File.OpenRead(registry.PublishedArchivePath(expected));
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            if (hash != expected) return StatusCode(StatusCodes.Status503ServiceUnavailable);
            archive = $"{Request.PathBase}/{name}/-/{Uri.EscapeDataString(name.Split('/')[^1] + "-" + version + ".tgz")}";
        }
        return Ok(new { ecosystem = "npm", name, version, mode = local == null ? "mirror" : "local", sha256 = hash, archive });
    }
}
