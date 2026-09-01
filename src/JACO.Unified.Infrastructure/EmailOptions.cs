namespace JACO.Unified.Infrastructure;

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string From { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
}
