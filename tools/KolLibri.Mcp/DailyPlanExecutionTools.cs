using System.Text.Json;

namespace KolLibri.Mcp;

/// <summary>
/// kol_plan_daily_adventures_execute (experimental, not offered by the HTTP host): the daily adventure planner of 0.4
/// moves onto the new library in a follow-up PR. Registered as before (same gate and rights), it answers with the
/// 0.5-dev notice and sends nothing.
/// </summary>
public partial class KolibriMcpServer
{
    private void RegisterDailyPlanExecutionTools()
    {
        Register("kol_plan_daily_adventures_execute",
            "Build (and optionally execute) a daily adventure plan from snapshots. Without execute it previews. Live path: execute=true returns a planId first, then the same call plus planId runs once (approvalToken only for an ungated host).",
            DailyPlanExecuteSchema(),
            NotYet("kol_plan_daily_adventures_execute"));
        Alias("daily-plan-execute", "kol_plan_daily_adventures_execute");
        Alias("daily_plan_execute", "kol_plan_daily_adventures_execute");
    }

    private static JsonElement DailyPlanExecuteSchema()
        => Schema("{\"type\":\"object\",\"properties\":{"
            + "\"role\":{\"enum\":[\"Main\",\"Mallbot\"]},"
            + "\"state\":{\"type\":\"object\"},"
            + "\"options\":{\"type\":\"object\"},"
            + "\"runIdPrefix\":{\"type\":\"string\"},"
            + "\"maxSteps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},"
            + "\"maxDurationSeconds\":{\"type\":\"integer\",\"minimum\":1},"
            + "\"execute\":{\"type\":\"boolean\",\"default\":false},"
            + "\"approvalToken\":{\"type\":\"string\"}"
            + "},\"additionalProperties\":true}");
}
