namespace Aiursoft.Nopkg.Registry;

public class RegistryOptions
{
    public string Upstream { get; set; } = "https://registry.npmjs.org/";
    public string StoragePath { get; set; } = Path.Combine(Path.GetTempPath(), "aiursoft-nopkg", "npm");
    public bool CacheOnly { get; set; }
    public S360AdmissionOptions S360 { get; set; } = new();
    public long MaxArchiveBytes { get; set; } = 512 * 1024 * 1024;
}
