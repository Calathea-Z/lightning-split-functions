using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;

namespace Functions.Helpers
{
    public sealed class CloudRoleNameInitializer : ITelemetryInitializer
    {
        private readonly string _roleName;
        public CloudRoleNameInitializer(string roleName) => _roleName = roleName;

        public void Initialize(ITelemetry telemetry) =>
            telemetry.Context.Cloud.RoleName = _roleName;
    }
}
