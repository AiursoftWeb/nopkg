namespace Aiursoft.Nopkg.Services.Publishing;
public sealed record PackagePublisher(string UserId, bool CanPublish, bool CanManagePackages, string? UserName = null);
public sealed class PublishingException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
