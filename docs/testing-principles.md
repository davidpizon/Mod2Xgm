# Testing principles

How a test in `tests/` is written. [testing.md](testing.md) says *what* is tested (layers, goldens, coverage, the
harness); this page says *how* each test is shaped. Decided in [ADR 0080](adr/0080-testing-principles.md); test doubles
use Moq ([ADR 0081](adr/0081-moq-for-test-doubles.md)).

Good unit tests are executable documentation and a safety net against regressions. To keep the suite from becoming
a maintenance burden, every test is **deterministic, isolated, and about behavior, not implementation**.

## Structure

### Arrange, Act, Assert

Every test runs in three phases, in this order, each marked with a comment. Don't interleave them.

- **Arrange:** build the object under test, its input data, and any stubs.
- **Act:** call the one method (or run the one behavior) the test is about.
- **Assert:** check the outcome.

```csharp
/// <summary>A note is logged at Warning level with its template unchanged.</summary>
[Fact]
public void Add_WithTemplateAndValues_LogsWarning()
{
    // Arrange
    CollectingSink sink = new();
    ConversionNotes sut = new(sink.CreateLogger());

    // Act
    sut.Add("Sample {Sample} truncated to {Length} bytes", "snare", 1024);

    // Assert
    LogEvent logged = Assert.Single(sink.Events);
    Assert.Equal(LogEventLevel.Warning, logged.Level);
}
```

- When Act and Assert are one expression (`Assert.Throws(() => sut.Read(bytes))`), mark the line
  `// Act & Assert`.
- A phase with nothing to do is left out. Don't write an empty `// Arrange`.
- A test with more than one Act phase is two tests.

### The system under test is `sut`

Name the object the test is about **`sut`** (system under test), or **`_sut`** when it's a field. That separates it
at a glance from its dependencies (`sink`, `reporter`, `mapper`) and from test data (`song`, `bytes`). Static classes
and static methods have no instance, so the call itself is the SUT (`ModReader.Read(bytes)`). There's no variable to
rename then.

## Naming

Test names read like a specification: **`Method_Scenario_ExpectedBehavior`**.

| | Example |
|---|---|
| 🔴 bad | `Test1`, `CheckDiscount`, `ReaderWorks` |
| 🟢 good | `Read_WithTruncatedSampleData_ThrowsInvalidInput` |

- **Method:** the member the Act phase calls (`Read`, `Add`, `Map`, `Render`). For a constructor use `Constructor`. For
  a property, use the property name. For a whole command-line run, use `Run`.
- **Scenario:** what's special about the input or the state, starting with `With`, `When`, `For`, `On` or `After`:
  `WithEmptySampleSlot`, `WhenOutputExists`, `ForFifteenSampleFile`.
- **Expected behavior:** what the caller sees: `ReturnsNull`, `ThrowsConversionException`, `LogsNote`,
  `KeepsLoop`.
- Use underscores only between the three parts.
- The XML doc comment (AGENTS.md) is still required. It says the same in a sentence, and *why* when that isn't
  obvious.
- Test classes are named `<TypeUnderTest>Tests`.

## Properties of a good test

### Fast and deterministic

- **In memory.** A unit test builds its input in code (`ModBuilder`, `DmfBuilder`, `Ym3438Script`, …) and never
  touches the file system, the network, the clock or the environment. See [Integration tests](#integration-tests) for
  the exceptions.
- **No shared state.** No mutable `static` fields, and no instance state carried from one test to the next. xUnit
  creates a new class instance per test, so put per-test setup in the constructor. A `static` member must be
  `readonly` and hold an immutable value (a `record`, a `const`, an `ImmutableArray`, a factory method).
- **No randomness without a fixed seed.** Use `new Random(seed)`, never `Random.Shared` or `Guid.NewGuid()`, in a test
  or in what it calls.
- **No order dependence.** Every test passes when run alone (`--filter-method`) and in any order.

### Isolated

- **One reason to fail.** A test checks one behavior. Several asserts are fine when they describe that one outcome
  (every field of one parsed header). Two unrelated outcomes are two tests.
- **Fake the boundaries, not the data.** Use Moq ([below](#test-doubles-with-moq)) for collaborators behind an interface:
  the reporter (`IConversionReporter`), progress (`IConversionProgress`, `IPhase`, `IWorkCounter`), a channel mapper
  (`IChannelMapper`), a DAC source (`IDacSource`). Never mock data (`Song`, `ModModule`, `FmPatch`, records) or maths
  (resamplers, FFTs, pitch tables): build the real thing.

### Resistant to refactoring

- **Behavior, not code.** Assert on what the caller can see: return values, thrown exceptions, conversion notes,
  bytes written, log events. Don't assert on how the result was computed.
- **Through the type's API.** Almost everything in `src/` is `internal` and visible to the tests through
  `InternalsVisibleTo`. Its "public API" here is its non-`private` members. Never reach a `private` member through
  reflection. If you want to test a private method, it probably belongs in its own type.
- **Fragile tests are bugs.** If a change to internals that leaves the output the same breaks a test, the test
  is coupled to the implementation. Fix the test.

## Test doubles with Moq

Moq is used where the SUT talks to an interface: stub **queries**, verify **commands**. Moq makes its proxies in a
dynamic assembly, so `src/Mod2Xgm.csproj` gives `DynamicProxyGenAssembly2` access to internals
([ADR 0081](adr/0081-moq-for-test-doubles.md)).

```csharp
// Arrange: stub a query with Setup + Returns.
Mock<IPhase> phase = new();
Mock<IConversionProgress> progress = new();
progress.Setup(p => p.Start(It.IsAny<string>(), It.IsAny<long>())).Returns(phase.Object);

// Match arguments loosely (It.IsAny) or exactly (a value, or It.Is with a condition).
progress.Setup(p => p.Start("render", It.Is<long>(n => n > 0))).Returns(phase.Object);

// Assert: verify a command happened, exactly as expected.
phase.Verify(p => p.Done(), Times.Once);
reporter.Verify(r => r.Completed(It.Is<RunOutcome>(o => o.Code == ExitCode.Success)), Times.Once);
```

Moq can't match `Span<T>` arguments (a `ref struct`), so `IDacSource` keeps its hand-written test sources.

- Use `MockBehavior.Default` (loose). Use `Strict` only when "nothing else was called" is the behavior under test.
- Verify only the calls the test is about, not every call the SUT happens to make.
- A hand-written fake is still fine when it *records* something to assert on in detail (`CollectingSink`
  keeps Serilog events). Don't rewrite one into a Moq setup that's harder to read.

## Assertions

Use the assertion that says what failed. The failure message is the test's only output.

| 🔴 weak | 🟢 strong |
|---|---|
| `Assert.True(result == 4)` (`Expected True, Actual False`) | `Assert.Equal(4, result)` (`Expected 4, Actual 2`) |
| `Assert.True(list.Count == 0)` | `Assert.Empty(list)` |
| `Assert.True(list.Contains(x))` | `Assert.Contains(x, list)` |
| `Assert.True(x is Foo)` | `Assert.IsType<Foo>(x)` |
| `Assert.True(a > b)` | `Assert.InRange(a, b + 1, int.MaxValue)` or `Assert.True(a > b, $"{a} <= {b}")` |
| `foreach (var x in xs) Assert.…` | `Assert.All(xs, x => Assert.…)` |

`Assert.True`/`False` stays for a real boolean result (`Assert.True(sut.IsLooped)`). For a comparison with no
dedicated assertion, `Assert.True(cond, message)` is fine when the message gives the values.

## Integration tests

Some tests need files or other programs: goldens (`Golden`), the DMF / YM3438 / SN76489 / PCM-driver probes, the
corpus, end-to-end CLI runs, and the harness tests. They are **integration tests**. They carry
`[Trait("Category", "Integration")]` on the class (or on the method, when only one test in the class needs it), and
only they may:

- read and write files, always under a `TempDirectory` (a fresh folder deleted on dispose) or read-only under
  `tests/golden/` / `tests/data/`;
- take longer than a few milliseconds (still under the 10-minute limit, [testing.md](testing.md#hung-tests)).

Everything else on this page applies to them too: AAA, `sut`, naming, determinism, no shared state.

Run only unit tests while iterating:

```
dotnet test --project tests/Mod2Xgm.Tests -- --filter-not-trait "Category=Integration"
```

## Long-running tests

**A test that takes 5 seconds or more** carries `[Trait("Speed", "Slow")]` on the method, whether it does I/O or
only heavy computing (most are FM fits). Pull request CI skips them; they run on pushes to `main`, every night and on
demand ([ADR 0082](adr/0082-defer-slow-tests-in-pr-ci.md)). Make a test faster before tagging it. Tag every test that
reads a shared `Lazy` fit too, not just the one measured slow: whichever runs first pays for the fit, so with the others
filtered out an untagged one would take its full time. Run what a pull request runs with:

```
dotnet test -- --filter-not-trait "Speed=Slow"
```

## Time budgets

**A test that checks a time budget** (such as the perceptual features' 20 ms per pitch region) carries
`[Trait("Category", "Performance")]` and measures the median of several runs after a warm-up. CI's coverage job skips
these: instrumentation makes code about 8 times slower, so the budget means nothing there
([ADR 0100](adr/0100-perceptual-features.md)). The build-and-test jobs still run them, in Release.

## Anti-patterns

- **The Mockery:** mocking everything until the test only shows that C# works. Mock boundaries, build data.
- **Logic in tests:** no `if`, `switch` or `for`/`foreach` **choosing what to assert** or **looping over cases**.
  Split the cases into a `[Theory]` with `[InlineData]` / `[MemberData]`, and use `Assert.All` for collections.
  A loop is allowed in **Arrange** when it builds input (filling a sample buffer, writing 64 pattern rows) or computes
  one expected value. Signal tests need that. Put such loops in a named helper when they're longer than a few lines.
- **The Liar:** a test that can't fail. It asserts nothing real, asserts `true`, or catches the exception it
  should check. Use `Assert.Throws`.
- **Hidden state:** a test that passes only after another test ran, or that depends on a file, an environment
  variable or a static that another test set.

## Checklist for a new or changed test

1. Name is `Method_Scenario_ExpectedBehavior` and the `/// <summary>` says the same.
2. `// Arrange`, `// Act`, `// Assert` (or `// Act & Assert`), in order, once each.
3. The object under test is `sut` (or the static call itself).
4. Input built in code. No I/O unless the test or its class is tagged `Category=Integration`. A test of 5 s or more is
   tagged `Speed=Slow`.
5. No mutable statics, no unseeded randomness, no clock.
6. Cases are `[Theory]` rows, not loops. Collections use `Assert.All`.
7. Assertions are the most specific available.
8. Interfaces are faked with Moq. Data and maths are real.
