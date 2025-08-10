namespace AutoMmo.Worker.Config;

public class WorkerConfig
{
    public bool IsEnabled { get; set; } = false;

    public string Login { get; set; } = "";

    public string Password { get; set; } = "";
}