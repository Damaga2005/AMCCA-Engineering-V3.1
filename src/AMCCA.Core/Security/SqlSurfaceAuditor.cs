using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AMCCA.Core.Security;

public static class SqlSurfaceAuditor
{
    private static readonly Regex ForbiddenMutationPattern = new(
        @"\b(UPDATE|DELETE)\s+(FROM\s+)?events\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> FindViolationsInAssembly(Assembly assembly)
    {
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            // Inspect string constants and static fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var field in fields)
            {
                if (field.FieldType == typeof(string) && field.IsStatic)
                {
                    var val = field.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(val) && ForbiddenMutationPattern.IsMatch(val))
                    {
                        violations.Add($"{type.FullName}.{field.Name}: {val}");
                    }
                }
            }
        }

        return violations;
    }
}
