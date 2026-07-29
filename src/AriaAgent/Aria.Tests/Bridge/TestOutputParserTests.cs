using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// run_tests output parsers: captured runner fixtures (green and red) per ecosystem, the generic
/// fallback, and the result formatter's caps (top-20 failures, output tail, maxOutput).
/// </summary>
public class TestOutputParserTests
{
    private static TestOutputParsers.ParsedTestRun Parse(TestOutputParsers.TestOutputKind kind, string output)
        => TestOutputParsers.Parse(kind, output);

    // ── dotnet test (VSTest console) ────────────────────────────────────────────

    private const string DotNetRed = """
        Determining projects to restore...
          All projects are up-to-date for restore.
        Test run for /repo/Aria.Tests/bin/Debug/net10.0/Aria.Tests.dll (.NETCoreApp,Version=v10.0)
        Microsoft (R) Test Execution Command Line Tool Version 17.12.0 (arm64)
        Starting test execution, please wait...
        [xUnit.net 00:00:01.234]     Aria.Tests.CartServiceTests.Checkout_EmptyCart_Throws [FAIL]
          Failed Aria.Tests.CartServiceTests.Checkout_EmptyCart_Throws [5 ms]
          Error Message:
           Expected CheckoutException, got null
          Stack Trace:
             at Aria.Tests.CartServiceTests.Checkout_EmptyCart_Throws() in /repo/Aria.Tests/CartService.cs:line 88

        Failed!  - Failed:     1, Passed:   181, Skipped:     0, Total:   182, Duration: 42 s
        """;

    [Fact]
    public void DotNet_Red_CountsAndFailure()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.DotNet, DotNetRed);

        Assert.Equal(181, r.Passed);
        Assert.Equal(1, r.Failed);
        Assert.Equal(0, r.Skipped);

        // The xUnit "[FAIL]" echo and the "Failed …" block are the same test — merged, not doubled.
        var f = Assert.Single(r.Failures);
        Assert.Equal("Aria.Tests.CartServiceTests.Checkout_EmptyCart_Throws", f.Name);
        Assert.Equal("CartService.cs:88", f.Location);
        Assert.Equal("Expected CheckoutException, got null", f.Message);
    }

    [Fact]
    public void DotNet_Green_CountsOnly()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.DotNet,
            "Passed!  - Failed:     0, Passed:   182, Skipped:     0, Total:   182, Duration: 12 s\n");

        Assert.Equal(182, r.Passed);
        Assert.Equal(0, r.Failed);
        Assert.Equal(0, r.Skipped);
        Assert.Empty(r.Failures);
    }

    [Fact]
    public void DotNet_LegacyVstestSummary()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.DotNet, """
            Test Run Failed.
            Total tests: 10
                 Passed: 8
                 Failed: 2
                Skipped: 0
             Total time: 1.234 Seconds
            """);

        Assert.Equal(8, r.Passed);
        Assert.Equal(2, r.Failed);
        Assert.Equal(0, r.Skipped);
    }

    // ── pytest ──────────────────────────────────────────────────────────────────

    private const string PytestRed = """
        ============================= test session starts ==============================
        platform darwin -- Python 3.12.2, pytest-8.1.1, pluggy-1.4.0
        collected 183 items

        tests/test_cart.py ..F                                                      [100%]

        =================================== FAILURES ===================================
        _______________________ test_checkout_empty_throws _______________________

            def test_checkout_empty_throws():
        >       with pytest.raises(CheckoutException):
        E       Failed: DID NOT RAISE <class 'CheckoutException'>

        tests/test_cart.py:88: Failed
        =========================== short test summary info ============================
        FAILED tests/test_cart.py::test_checkout_empty_throws - Failed: DID NOT RAISE <class 'CheckoutException'>
        ========================= 1 failed, 182 passed in 42.31s =========================
        """;

    [Fact]
    public void Pytest_Red_CountsAndFailure()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Pytest, PytestRed);

        Assert.Equal(182, r.Passed);
        Assert.Equal(1, r.Failed);

        var f = Assert.Single(r.Failures);
        Assert.Equal("tests/test_cart.py::test_checkout_empty_throws", f.Name);
        Assert.Equal("tests/test_cart.py:88", f.Location);
        Assert.Equal("Failed: DID NOT RAISE <class 'CheckoutException'>", f.Message);
    }

    [Fact]
    public void Pytest_Green_CountsOnly()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Pytest,
            "============================= 182 passed in 12.30s ==============================\n");

        Assert.Equal(182, r.Passed);
        Assert.Null(r.Failed);
        Assert.Empty(r.Failures);
    }

    // ── jest / vitest ───────────────────────────────────────────────────────────

    private const string JestRed = """
         FAIL  src/cart.test.js
          ● CartService › checkout empty cart throws

            expect(received).toThrow(expected)

            Expected constructor: CheckoutException

              86 |   it('checkout empty cart throws', () => {
            > 88 |      expect(() => checkout(empty)).toThrow(CheckoutException)
                 |                                        ^
              89 |   })

              at Object.<anonymous> (src/cart.test.js:88:44)

         PASS  src/util.test.js
        Tests:       1 failed, 181 passed, 182 total
        """;

    [Fact]
    public void Jest_Red_CountsAndFailure()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Jest, JestRed);

        Assert.Equal(181, r.Passed);
        Assert.Equal(1, r.Failed);

        var f = Assert.Single(r.Failures);
        Assert.Equal("CartService › checkout empty cart throws", f.Name);
        Assert.Equal("src/cart.test.js:88", f.Location);   // file:line beats the bare FAIL file
        Assert.Equal("expect(received).toThrow(expected)", f.Message);
    }

    [Fact]
    public void Vitest_Red_CrossMarkersAndPipeSummary()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Jest, """
             ✕ src/cart.test.ts > CartService > checkout empty cart throws
             ✓ src/util.test.ts > formatMoney
             Tests  1 failed | 9 passed (10)
            """);

        Assert.Equal(9, r.Passed);
        Assert.Equal(1, r.Failed);
        var f = Assert.Single(r.Failures);
        Assert.Equal("src/cart.test.ts > CartService > checkout empty cart throws", f.Name);
    }

    // ── cargo test ──────────────────────────────────────────────────────────────

    private const string CargoRed = """
           Compiling demo v0.1.0 (/repo/demo)
            Finished test [unoptimized + debuginfo] target(s) in 1.2s
             Running unittests src/lib.rs (target/debug/deps/demo-abc)

        running 3 tests
        test cart::tests::checkout_ok ... ok
        test cart::tests::checkout_empty_throws ... FAILED
        test cart::tests::apply_coupon_negative ... FAILED

        failures:

        ---- cart::tests::checkout_empty_throws stdout ----
        thread 'cart::tests::checkout_empty_throws' panicked at src/cart.rs:88:9:
        Expected CheckoutException, got null
        note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace

        ---- cart::tests::apply_coupon_negative stdout ----
        thread 'cart::tests::apply_coupon_negative' panicked at src/cart.rs:102:5:
        assertion failed: coupon > 0

        failures:
            cart::tests::checkout_empty_throws
            cart::tests::apply_coupon_negative

        test result: FAILED. 1 passed; 2 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.03s
        """;

    [Fact]
    public void Cargo_Red_CountsAndFailures()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Cargo, CargoRed);

        Assert.Equal(1, r.Passed);
        Assert.Equal(2, r.Failed);
        Assert.Equal(0, r.Skipped);

        Assert.Equal(2, r.Failures.Count);
        Assert.Equal("cart::tests::checkout_empty_throws", r.Failures[0].Name);
        Assert.Equal("src/cart.rs:88", r.Failures[0].Location);
        Assert.Equal("Expected CheckoutException, got null", r.Failures[0].Message);
        Assert.Equal("cart::tests::apply_coupon_negative", r.Failures[1].Name);
        Assert.Equal("src/cart.rs:102", r.Failures[1].Location);
        Assert.Equal("assertion failed: coupon > 0", r.Failures[1].Message);
    }

    [Fact]
    public void Cargo_Green_CountsOnly()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Cargo,
            "test result: ok. 3 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n");

        Assert.Equal(3, r.Passed);
        Assert.Equal(0, r.Failed);
        Assert.Empty(r.Failures);
    }

    // ── go test ─────────────────────────────────────────────────────────────────

    private const string GoRed = """
        --- FAIL: TestCheckoutEmpty (0.00s)
            cart_test.go:88: Expected CheckoutException, got null
        --- FAIL: TestApplyCoupon (0.00s)
            coupon_test.go:12: assertion failed
        FAIL
        FAIL	example.com/demo/cart	0.5s
        FAIL
        """;

    [Fact]
    public void Go_Red_FailuresWithLocations()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Go, GoRed);

        Assert.Null(r.Passed);   // go test prints no passed count
        Assert.Equal(2, r.Failed);

        Assert.Equal(2, r.Failures.Count);
        Assert.Equal("TestCheckoutEmpty", r.Failures[0].Name);
        Assert.Equal("cart_test.go:88", r.Failures[0].Location);
        Assert.Equal("Expected CheckoutException, got null", r.Failures[0].Message);
        Assert.Equal("TestApplyCoupon", r.Failures[1].Name);
        Assert.Equal("coupon_test.go:12", r.Failures[1].Location);
    }

    [Fact]
    public void Go_Green_NoCounts()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Go, "ok  \texample.com/demo\t0.5s\n");

        Assert.Null(r.Passed);
        Assert.Null(r.Failed);
        Assert.Empty(r.Failures);
    }

    // ── generic fallback ────────────────────────────────────────────────────────

    [Fact]
    public void Generic_YieldsNoStructure()
    {
        var r = Parse(TestOutputParsers.TestOutputKind.Generic, "anything at all\n");
        Assert.Null(r.Passed);
        Assert.Null(r.Failed);
        Assert.Empty(r.Failures);
    }

    // ── formatter ───────────────────────────────────────────────────────────────

    [Fact]
    public void Format_Failure_CountsFailuresAndTail()
    {
        var parsed = new TestOutputParsers.ParsedTestRun(181, 1, 0,
            [new TestOutputParsers.TestFailure("Foo.Bar_Baz", "Foo.cs:42", "boom")]);

        var text = TestOutputParsers.FormatResult(
            "dotnet test", parsed, 1, TimeSpan.FromSeconds(42.3), "line one\nline two", 4000);

        Assert.Contains("◈ TEST RUN [dotnet test] — FAILED (exit 1, 42.3s)", text);
        Assert.Contains("passed: 181  failed: 1  skipped: 0", text);
        Assert.Contains("✗ Foo.Bar_Baz — Foo.cs:42", text);
        Assert.Contains("  boom", text);
        Assert.Contains("— output tail (last 17 chars) —", text);
        Assert.Contains("line one\nline two", text);
    }

    [Fact]
    public void Format_Success_NoTail()
    {
        var parsed = new TestOutputParsers.ParsedTestRun(182, 0, 0, []);

        var text = TestOutputParsers.FormatResult(
            "dotnet test", parsed, 0, TimeSpan.FromSeconds(12), "lots\nof\noutput", 4000);

        Assert.Contains("— PASSED (exit 0,", text);
        Assert.Contains("passed: 182", text);
        Assert.DoesNotContain("output tail", text);
        Assert.DoesNotContain("lots", text);   // success stays at header + counts
    }

    [Fact]
    public void Format_CapsFailuresAt20()
    {
        var failures = Enumerable.Range(1, 25)
            .Select(i => new TestOutputParsers.TestFailure($"Suite.Test_{i}", null, null))
            .ToList();
        var parsed = new TestOutputParsers.ParsedTestRun(0, 25, 0, failures);

        var text = TestOutputParsers.FormatResult("cargo test", parsed, 101, TimeSpan.Zero, "", 4000);

        Assert.Contains("✗ Suite.Test_20", text);
        Assert.DoesNotContain("✗ Suite.Test_21", text);
        Assert.Contains("… and 5 more failures", text);
    }

    [Fact]
    public void Format_RespectsMaxOutput()
    {
        var parsed = new TestOutputParsers.ParsedTestRun(null, null, null, []);
        var huge = new string('x', 10000);

        var text = TestOutputParsers.FormatResult("make test", parsed, 1, TimeSpan.Zero, huge, 500);

        Assert.Equal(500, text.Length);
        Assert.EndsWith("…", text);
    }
}
