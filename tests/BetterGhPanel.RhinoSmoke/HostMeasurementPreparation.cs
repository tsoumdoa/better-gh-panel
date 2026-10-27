using System;
using System.Reflection;
using BetterGhPanel.Core;

// Host queue tests that expect retention must supply explicit immutable proofs.
// This synthetic witness is for these test delegates only, never live geometry.
internal static class HostMeasurementPreparation
{
    internal static object Resolver(Assembly assembly, Func<int, Func<PanelGeometryInfo?>?> operations)
    {
        Type witnessType = assembly.GetType("BetterGhPanel.Runtime.MeasurementWitness", true)!;
        Type preparedType = assembly.GetType("BetterGhPanel.Runtime.PreparedMeasurement`1", true)!.MakeGenericType(typeof(PanelGeometryInfo));
        var create = typeof(HostMeasurementPreparation).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(preparedType);
        return create.Invoke(null, new object[] { witnessType, preparedType, operations })!;
    }

    private static Func<int, T?> Create<T>(Type witnessType, Type preparedType, Func<int, Func<PanelGeometryInfo?>?> operations) where T : class
    {
        var witnessConstructor = witnessType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        var preparedConstructor = preparedType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return position =>
        {
            var operation = operations(position);
            if (operation == null) return null;
            object witness = witnessConstructor.Invoke(new object[] { "host-test/position", BitConverter.GetBytes(position) });
            return (T)preparedConstructor.Invoke(new object?[] { operation, null, witness });
        };
    }
}
