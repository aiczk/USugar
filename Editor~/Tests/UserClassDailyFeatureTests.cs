using Xunit;

namespace USugar.Tests;

public class UserClassDailyFeatureTests
{
    [Fact]
    public void InstanceMethodGroup_CapturesClassReceiver()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DailyCounter {
  public int value;
  public int Add(int x) { return value + x; }
}
public class DailyMethodGroup : UdonSharpBehaviour {
  public int result;
  void Start() {
    var counter = new DailyCounter { value = 4 };
    Func<int, int> add = counter.Add;
    result = add(3);
  }
}
", "DailyMethodGroup");

    [Fact]
    public void OperatorsAndConversions_OnUserClassCompile()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class DailyNumber {
  public int value;
  public static DailyNumber operator +(DailyNumber a, DailyNumber b)
    => new DailyNumber { value = a.value + b.value };
  public static implicit operator int(DailyNumber n) => n.value;
}
public class DailyOperators : UdonSharpBehaviour {
  public int result;
  void Start() {
    var a = new DailyNumber { value = 2 };
    var b = new DailyNumber { value = 5 };
    result = a + b;
  }
}
", "DailyOperators");

    [Fact]
    public void Deconstruct_OnUserClassCompiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class DailyPair {
  public int x;
  public string y;
  public void Deconstruct(out int a, out string b) { a = x; b = y; }
}
public class DailyDeconstruct : UdonSharpBehaviour {
  public int result;
  void Start() {
    var pair = new DailyPair { x = 8, y = ""ok"" };
    var (a, b) = pair;
    result = a + b.Length;
  }
}
", "DailyDeconstruct");

    [Fact]
    public void PositionalPattern_OnUserClassCompiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class DailyPatternPair {
  public int x;
  public int y;
  public void Deconstruct(out int a, out int b) { a = x; b = y; }
}
public class DailyPattern : UdonSharpBehaviour {
  public bool result;
  void Start() {
    var pair = new DailyPatternPair { x = 3, y = 9 };
    result = pair is (3, > 5);
  }
}
", "DailyPattern");

    [Fact]
    public void CustomEvent_OnUserClassCompiles()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class DailyEventSource {
  Action handlers;
  public event Action Changed { add { handlers += value; } remove { handlers -= value; } }
  public void Fire() { handlers?.Invoke(); }
}
public class DailyCustomEvent : UdonSharpBehaviour {
  public int result;
  void OnChanged() { result++; }
  void Start() {
    var source = new DailyEventSource();
    source.Changed += OnChanged;
    source.Fire();
    source.Changed -= OnChanged;
  }
}
", "DailyCustomEvent");
}
