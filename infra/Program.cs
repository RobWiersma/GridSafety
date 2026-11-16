using Amazon.CDK;

namespace GridSafety.Infra;

public static class Program
{
    public static void Main()
    {
        var app = new App();
        var stage = (string?)app.Node.TryGetContext("stage") ?? "dev";

        new GridSafetyStack(app, $"GridSafety-{stage}", new GridSafetyStackProps
        {
            Stage = stage,
            OpsEmail = (string?)app.Node.TryGetContext("opsEmail"),
            MonthlyBudgetUsd = double.TryParse(app.Node.TryGetContext("monthlyBudgetUsd")?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var budget) ? budget : 5,
            DomainName = (string?)app.Node.TryGetContext("domainName"),
            CertificateArn = (string?)app.Node.TryGetContext("certificateArn"),
            Description = $"GridSafety drone power-line inspection platform ({stage})",
            Env = new Amazon.CDK.Environment
            {
                Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
                Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION"),
            },
        });

        Tags.Of(app).Add("project", "gridsafety");
        Tags.Of(app).Add("stage", stage);
        app.Synth();
    }
}
