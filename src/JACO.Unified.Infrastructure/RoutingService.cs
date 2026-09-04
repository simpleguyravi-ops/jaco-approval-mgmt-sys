using System.Text.Json;
using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Infrastructure;

public sealed record RouteResult(bool Ok, string OutcomeCode, string? Detail, int? RoutingRuleId, int? WorkflowVersionId, string? MatchedRuleName, Dictionary<int, List<int>> ApproverIds);

// Resolves which RoutingRule (and therefore which approver chain) a submission matches,
// by evaluating each active rule's criteria (in priority order, first full match wins)
// against the submitted routing context. Unchanged in shape from the original Approval
// engine's RoutingService -- this logic was already fully generic per Approval Type.
public sealed class RoutingService(UnifiedDbContext db)
{
    public static readonly string[] Operators = ["=", "!=", "CONTAINS", "STARTSWITH", "ENDSWITH", "IN", ">", ">=", "<", "<="];

    public async Task<RouteResult> ResolveAsync(int approvalTypeId, Dictionary<string, JsonElement> routingContext)
    {
        var version = await db.WorkflowVersions.SingleOrDefaultAsync(v => v.ApprovalTypeId == approvalTypeId && v.IsCurrent);
        if (version is null)
            return new RouteResult(false, "NoRulesConfigured", "No current workflow version configured for this Approval Type.", null, null, null, new());

        var rules = await db.RoutingRules
            .Where(r => r.WorkflowVersionId == version.Id && r.Active)
            .OrderBy(r => r.Priority)
            .ToListAsync();

        if (rules.Count == 0)
            return new RouteResult(false, "NoRulesConfigured", "No active routing rules configured.", null, null, null, new());

        foreach (var rule in rules)
        {
            var criteria = await db.RoutingRuleCriteria.Where(c => c.RoutingRuleId == rule.Id).ToListAsync();
            if (EvaluateAll(criteria, routingContext))
            {
                var steps = await db.WorkflowSteps.Where(s => s.RoutingRuleId == rule.Id).OrderBy(s => s.LevelNo).ToListAsync();
                if (steps.Count == 0)
                    return new RouteResult(false, "NoApproversConfigured", $"Rule '{rule.RuleName}' matched but has no approval levels configured.", rule.Id, version.Id, rule.RuleName, new());

                var approverIds = new Dictionary<int, List<int>>();
                foreach (var step in steps)
                {
                    var ids = await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == step.Id).Select(a => a.UserId).ToListAsync();
                    if (ids.Count == 0)
                        return new RouteResult(false, "NoApproversConfigured", $"Level {step.LevelNo} of rule '{rule.RuleName}' has no approvers configured.", rule.Id, version.Id, rule.RuleName, new());
                    approverIds[step.LevelNo] = ids;
                }
                return new RouteResult(true, "Routed", null, rule.Id, version.Id, rule.RuleName, approverIds);
            }
        }

        return new RouteResult(false, "NoRuleMatched", "No routing rule's criteria matched the submitted data.", null, version.Id, null, new());
    }

    // Splits a rule's criteria into OR-separated groups of consecutive AND-joined rows,
    // ordered by SortOrder -- i.e. standard "AND binds tighter than OR" precedence, so
    // "A OR B AND C" groups as [A], [B, C] (meaning A OR (B AND C)), never (A OR B) AND C.
    // A row's LogicalOperator is only consulted from the second row onward; the first row of
    // a rule (and the first row of any new group) always starts a fresh group regardless of
    // what its own LogicalOperator happens to be.
    public static List<List<RoutingRuleCriteria>> GroupByPrecedence(IEnumerable<RoutingRuleCriteria> criteria)
    {
        var groups = new List<List<RoutingRuleCriteria>>();
        foreach (var c in criteria.OrderBy(c => c.SortOrder))
        {
            if (groups.Count == 0 || string.Equals(c.LogicalOperator, "OR", StringComparison.OrdinalIgnoreCase))
                groups.Add([]);
            groups[^1].Add(c);
        }
        return groups;
    }

    public static bool EvaluateAll(IEnumerable<RoutingRuleCriteria> criteria, Dictionary<string, JsonElement> context)
    {
        var groups = GroupByPrecedence(criteria);
        return groups.Count == 0 || groups.Any(g => g.All(c => Evaluate(c, context)));
    }

    public static bool Evaluate(RoutingRuleCriteria criteria, Dictionary<string, JsonElement> context)
    {
        if (!context.TryGetValue(criteria.FieldKey, out var element)) return false;
        var actual = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        var expected = criteria.ComparisonValue;

        switch (criteria.Operator)
        {
            case "=": return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            case "!=": return !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            case "CONTAINS": return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
            case "STARTSWITH": return actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
            case "ENDSWITH": return actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase);
            case "IN": return expected.Split(',').Select(v => v.Trim()).Any(v => string.Equals(v, actual, StringComparison.OrdinalIgnoreCase));
            case ">" or ">=" or "<" or "<=":
                if (decimal.TryParse(actual, out var an) && decimal.TryParse(expected, out var en))
                    return Compare(criteria.Operator, an.CompareTo(en));
                if (DateTime.TryParse(actual, out var ad) && DateTime.TryParse(expected, out var ed))
                    return Compare(criteria.Operator, ad.CompareTo(ed));
                return false;
            default: return false;
        }
    }

    static bool Compare(string op, int cmp) => op switch
    {
        ">" => cmp > 0,
        ">=" => cmp >= 0,
        "<" => cmp < 0,
        "<=" => cmp <= 0,
        _ => false
    };
}
