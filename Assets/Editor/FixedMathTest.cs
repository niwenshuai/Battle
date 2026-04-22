using UnityEngine;
using UnityEditor;
using FrameSync;

public static class FixedMathTest
{
    [MenuItem("Tools/Test FixedMath")]
    public static void Run()
    {
        // 测试 Sqrt
        TestSqrt(0, 0f);
        TestSqrt(1, 1f);
        TestSqrt(4, 2f);
        TestSqrt(9, 3f);
        TestSqrt(25, 5f);
        TestSqrt(100, 10f);

        // 测试 Distance((-5,0), (5,0)) = 10
        var a = new FixedVector2(FixedInt.FromInt(-5), FixedInt.Zero);
        var b = new FixedVector2(FixedInt.FromInt(5), FixedInt.Zero);
        var dist = FixedVector2.Distance(a, b);
        Debug.Log($"[Test] Distance((-5,0),(5,0)) = {dist.ToFloat():F4} (expected 10.0000)");

        // 测试 Normalized
        var diff = b - a; // (10, 0)
        var norm = diff.Normalized;
        Debug.Log($"[Test] Normalized(10,0) = ({norm.X.ToFloat():F4}, {norm.Y.ToFloat():F4}) (expected 1.0, 0.0)");

        // 测试除法
        var ten = FixedInt.FromInt(10);
        var three = FixedInt.FromInt(3);
        var divResult = ten / three;
        Debug.Log($"[Test] 10 / 3 = {divResult.ToFloat():F4} (expected 3.3333)");

        var hundred = FixedInt.FromInt(100);
        var seven = FixedInt.FromInt(7);
        Debug.Log($"[Test] 100 / 7 = {(hundred / seven).ToFloat():F4} (expected 14.2857)");

        Debug.Log("[Test] FixedMath 测试完成!");
    }

    static void TestSqrt(int input, float expected)
    {
        var v = FixedInt.FromInt(input);
        var result = FixedInt.Sqrt(v);
        Debug.Log($"[Test] Sqrt({input}) = {result.ToFloat():F4} (expected {expected:F4})");
    }
}
