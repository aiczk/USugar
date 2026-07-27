using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace USugar.Tests;

public static class TestHelper
{
    public static readonly UdonAbiCatalog RegistryFacts
        = ExternRegistry.LoadTypedCatalog();
    internal static readonly BoundAbiPlan BoundRegistryFacts
        = BoundAbiPlan.ExactCatalog(RegistryFacts);

    public static BoundExtern BindExtern(string signature)
        => UdonAbiCatalog.FromNamesForTests(new[] { signature }).Require(AbiKey(signature));

    /// <summary>
    /// Test fixtures intentionally start from flat registry names. Production
    /// never performs this reverse parse; its authority is UdonNodeDefinition.
    /// </summary>
    public static UdonAbiKey AbiKey(string signature)
    {
        var ownerEnd = signature.IndexOf(".__", System.StringComparison.Ordinal);
        if (ownerEnd <= 0)
            throw new System.ArgumentException("Invalid test extern name.", nameof(signature));
        var owner = signature.Substring(0, ownerEnd);
        var segments = signature.Substring(ownerEnd + 3)
            .Split(new[] { "__" }, System.StringSplitOptions.None);
        if (segments.Length == 2)
        {
            if (segments[0].StartsWith("set_", System.StringComparison.Ordinal))
                return UdonAbiKey.OmittedResult(owner, segments[0], segments[1]);
            return UdonAbiKey.Method(owner, segments[0], segments[1]);
        }
        if (segments.Length == 3 && segments[0] == "ctor")
            return UdonAbiKey.Constructor(owner,
                segments[1].Length == 0
                    ? System.Array.Empty<string>()
                    : segments[1].Split('_'),
                segments[2]);
        if (segments.Length == 3)
            return UdonAbiKey.Method(owner, segments[0],
                segments[1].Split('_'), segments[2]);
        throw new System.ArgumentException("Invalid test extern name.", nameof(signature));
    }

    public static string RegistryOwner(string signature)
    {
        var separator = signature.IndexOf('.');
        return separator > 0
            ? signature.Substring(0, separator)
            : signature;
    }

    public static string StubSource => Stubs;

    static readonly MetadataReference[] _standardRefs = BuildStandardRefs();
    public static MetadataReference[] StandardRefs => _standardRefs;

    static MetadataReference[] BuildStandardRefs()
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        };
        var runtimePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location),
            "System.Runtime.dll");
        if (System.IO.File.Exists(runtimePath))
            refs.Add(MetadataReference.CreateFromFile(runtimePath));
        return refs.ToArray();
    }

    const string Stubs = @"
namespace UdonSharp
{
    public enum BehaviourSyncMode
    {
        Any,
        None,
        NoVariableSync,
        Continuous,
        Manual,
    }
    public class UdonBehaviourSyncModeAttribute : System.Attribute
    {
        public UdonBehaviourSyncModeAttribute(BehaviourSyncMode mode) { }
    }
    public class UdonSyncedAttribute : System.Attribute { }
    public class RecursiveMethodAttribute : System.Attribute { }
    public class FieldChangeCallbackAttribute : System.Attribute { public FieldChangeCallbackAttribute(string s) { } }
    public class UdonSharpBehaviour : UnityEngine.MonoBehaviour
    {
        public void RequestSerialization() { }
        public void SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget target, string eventName, params object[] parameters) { }
        public virtual void OnPlayerJoined(VRC.SDKBase.VRCPlayerApi player) { }
        public virtual void OnPlayerLeft(VRC.SDKBase.VRCPlayerApi player) { }
        public virtual void OnDeserialization() { }
        public virtual void Interact() { }
        public virtual void OnPickup() { }
        public virtual void OnDrop() { }
        public virtual void OnOwnershipTransferred(VRC.SDKBase.VRCPlayerApi player) { }
        public virtual bool OnOwnershipRequest(VRC.SDKBase.VRCPlayerApi requestingPlayer, VRC.SDKBase.VRCPlayerApi requestedOwner) => true;
        public virtual void OnVideoPlay() { }
        public virtual void OnVideoReady() { }
        public virtual void OnVideoEnd() { }
        public void SendCustomEvent(string eventName) { }
        public void SendCustomEventDelayedSeconds(string eventName, float seconds, VRC.Udon.Common.Enums.EventTiming timing = VRC.Udon.Common.Enums.EventTiming.Update) { }
        public void SendCustomEventDelayedFrames(string eventName, int frames, VRC.Udon.Common.Enums.EventTiming timing = VRC.Udon.Common.Enums.EventTiming.Update) { }
        public void SetProgramVariable(string name, object value) { }
        public object GetProgramVariable(string name) => null;
        public bool DisableInteractive { get; set; }
    }
}
namespace UdonSharp.Tests
{
    public class IntegrationTestSuite : UdonSharp.UdonSharpBehaviour
    {
        public void TestAssertion(string name, bool condition) { }
    }
}
namespace UnityEngine
{
    public enum HideFlags { None, HideInHierarchy }
    public class Object
    {
        public string name { get; set; }
        public static void Destroy(Object obj) { }
        public static void DestroyImmediate(Object obj) { }
        public static T Instantiate<T>(T original) where T : Object => default;
        public static GameObject Instantiate(GameObject original, Transform parent) => default;
        public static GameObject Instantiate(GameObject original, Vector3 position, Quaternion rotation) => default;
        public static GameObject Instantiate(GameObject original, Transform parent, bool worldPositionStays) => default;
        public static GameObject Instantiate(GameObject original, Vector3 position, Quaternion rotation, Transform parent) => default;
        public static Object Instantiate(Object original) => default;
        public static implicit operator bool(Object o) => o != null;
    }
    public class Component : Object
    {
        public GameObject gameObject { get; }
        public Transform transform { get; }
        public T GetComponent<T>() => default;
        public Component GetComponent(System.Type type) => default;
        public T GetComponentInChildren<T>() => default;
        public T GetComponentInChildren<T>(bool includeInactive) => default;
        public T[] GetComponentsInChildren<T>() => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => default;
        public T GetComponentInParent<T>() => default;
        public T GetComponentInParent<T>(bool includeInactive) => default;
        public T[] GetComponentsInParent<T>() => default;
        public T[] GetComponentsInParent<T>(bool includeInactive) => default;
        public T[] GetComponents<T>() => default;
        public Component[] GetComponents(System.Type type) => default;
    }
    public class Behaviour : Component { public bool enabled { get; set; } }
    public class MonoBehaviour : Behaviour { }
    public class Shader : Object { }
    public class Material : Object
    {
        public Shader shader { get; set; }
    }
    public class GameObject : Object
    {
        public bool activeSelf { get; }
        public void SetActive(bool value) { }
        public Transform transform { get; }
        public T GetComponent<T>() => default;
        public Component GetComponent(System.Type type) => default;
        public T[] GetComponents<T>() => default;
        public Component[] GetComponents(System.Type type) => default;
        public T[] GetComponentsInChildren<T>() => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => default;
        public static GameObject Find(string name) => default;
    }
    public class Transform : Component, System.Collections.IEnumerable
    {
        public Vector3 position { get; set; }
        public Vector3 localPosition { get; set; }
        public Quaternion rotation { get; set; }
        public Vector3 forward { get; set; }
        public Transform parent { get; set; }
        public int childCount => 0;
        public Transform GetChild(int index) => default;
        public void SetParent(Transform parent) { }
        public void SetParent(Transform parent, bool worldPositionStays) { }
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation) { }
        public System.Collections.IEnumerator GetEnumerator() => default;
    }
    public enum HumanBodyBones { Hips, Head, LeftHand, RightHand }
    public struct Vector2 {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float this[int i] { get => 0; set { } }
        public static Vector2 operator +(Vector2 a, Vector2 b) => default;
        public static Vector2 operator -(Vector2 a, Vector2 b) => default;
        public static Vector2 operator *(Vector2 a, float d) => default;
        public static Vector2 operator *(float d, Vector2 a) => default;
    }
    public struct Vector3 {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; this.z = 0; }
        public float this[int i] { get => 0; set { } }
        public float magnitude => 0;
        public Vector3 normalized => default;
        public static Vector3 operator +(Vector3 a, Vector3 b) => default;
        public static Vector3 operator -(Vector3 a, Vector3 b) => default;
        public static Vector3 operator *(Vector3 a, float d) => default;
        public static Vector3 operator *(float d, Vector3 a) => default;
        public static Vector3 operator -(Vector3 a) => default;
        public static bool operator ==(Vector3 a, Vector3 b) => true;
        public static bool operator !=(Vector3 a, Vector3 b) => false;
        public static implicit operator Vector3(Vector2 v) => default;
        public static Vector3 zero => default;
        public static Vector3 one => default;
        public static Vector3 up => default;
        public static Vector3 right => default;
        public static Vector3 forward => default;
        public static float Dot(Vector3 a, Vector3 b) => 0;
        public static Vector3 Project(Vector3 a, Vector3 b) => default;
        public static float Distance(Vector3 a, Vector3 b) => 0;
        public void Normalize() { }
    }
    public struct Vector4 {
        public float x, y, z, w;
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public Vector4(float x, float y, float z) { this.x = x; this.y = y; this.z = z; this.w = 0; }
        public Vector4(float x, float y) { this.x = x; this.y = y; this.z = 0; this.w = 0; }
        public float this[int i] { get => 0; set { } }
    }
    public struct Vector2Int {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public int this[int i] { get => 0; set { } }
    }
    public struct Vector3Int {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public int this[int i] { get => 0; set { } }
    }
    public struct Matrix4x4 {
        public static Matrix4x4 identity => default;
        public float this[int index] { get => 0; set { } }
        public float this[int row, int column] { get => 0; set { } }
    }
    public struct Quaternion {
        public float x, y, z, w;
        public static Quaternion identity => default;
        public static Quaternion AngleAxis(float angle, Vector3 axis) => default;
        public void ToAngleAxis(out float angle, out Vector3 axis) { angle = 0; axis = Vector3.zero; }
    }
    public struct Color {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1,1,1,1);
        public static Color green => new Color(0,1,0,1);
        public static bool operator ==(Color a, Color b) => true;
        public static bool operator !=(Color a, Color b) => false;
    }
    public struct CombineInstance { public Mesh mesh { get; set; } public Matrix4x4 transform { get; set; } }
    public class Mesh : Object { }
    public class MeshFilter : Component { public Mesh mesh { get; set; } public Mesh sharedMesh { get; set; } }
    public class Camera : Behaviour { public CameraClearFlags clearFlags { get; set; } }
    public class Rigidbody : Component {
        public RigidbodyConstraints constraints { get; set; }
        public Vector3 velocity { get; set; }
        public void AddForce(float x, float y, float z) { }
    }
    public class MaterialPropertyBlock {
        public void SetColor(int nameID, Color value) { }
        public void SetColor(string name, Color value) { }
    }
    public enum CameraClearFlags { Skybox = 1, Color = 2, Depth = 3 }
    public enum RigidbodyConstraints { None = 0, FreezePositionX = 2, FreezePositionY = 4, FreezePositionZ = 8, FreezePosition = 14, FreezeAll = 126 }
    public enum KeyCode { A, Space = 32, Return = 13 }
    public class Input {
        public static bool GetKeyDown(KeyCode k) => false;
        public static bool GetKey(KeyCode k) => false;
    }
    public static class Time { public static int frameCount => 0; public static float fixedDeltaTime { get; set; } }
    public static class Random { public static void InitState(int s) { } public static int Range(int a, int b) => 0; }
    public static class Debug { public static void Log(object msg) { } public static void LogWarning(object msg) { } public static void LogError(object msg) { } }
    public struct Mathf {
        public static int Max(int a, int b) => a;
        public static int Min(int a, int b) => a;
        public static float Max(float a, float b) => a;
        public static float Min(float a, float b) => a;
        public static float Clamp(float v, float min, float max) => v;
        public static float Clamp01(float f) => f;
        public static float Abs(float f) => f;
        public static float Sign(float f) => 1;
        public static bool Approximately(float a, float b) => true;
        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime) => current;
    }
    public struct Ray {
        public Vector3 origin { get; set; }
        public Vector3 direction { get; set; }
        public Ray(Vector3 origin, Vector3 direction) { this.origin = origin; this.direction = direction; }
    }
    public class Collider : Component { public bool enabled { get; set; } }
    public class BoxCollider : Collider { }
    public class Sprite : Object { }
    public class SpriteRenderer : Renderer { public Sprite sprite { get; set; } }
    public class Renderer : Component {
        public int sortingOrder;
        public void GetPropertyBlock(MaterialPropertyBlock block) { }
        public void SetPropertyBlock(MaterialPropertyBlock block) { }
    }
    public class MeshRenderer : Renderer { }
    public class WheelCollider : Component {
        public float steerAngle { get; set; }
        public float motorTorque { get; set; }
        public float brakeTorque { get; set; }
    }
    public class AddComponentMenuAttribute : System.Attribute { public AddComponentMenuAttribute(string s) { } }
    public class HideInInspectorAttribute : System.Attribute { }
    public class SerializeFieldAttribute : System.Attribute { }
    public class TooltipAttribute : System.Attribute { public TooltipAttribute(string s) { } }
    public class HeaderAttribute : System.Attribute { public HeaderAttribute(string s) { } }
    public class RangeAttribute : System.Attribute { public RangeAttribute(float min, float max) { } }
    public class TextAreaAttribute : System.Attribute { public TextAreaAttribute() { } public TextAreaAttribute(int a, int b) { } }
}
namespace UnityEngine.Rendering {
    public enum ReflectionProbeType { Cube, Card }
    public struct SphericalHarmonicsL2 { public float this[int i, int c] { get => 0; set { } } }
}
namespace VRC.SDKBase {
    public static class VRCShader { public static int PropertyToID(string name) => 0; }
}
namespace UnityEngine.UI {
    public class Graphic : UnityEngine.Component { public UnityEngine.Color color { get; set; } }
    public class Text : Graphic { public string text { get; set; } }
    public class Image : Graphic { public UnityEngine.Sprite sprite { get; set; } }
    public struct ColorBlock {
        public UnityEngine.Color normalColor { get; set; }
        public UnityEngine.Color highlightedColor { get; set; }
        public UnityEngine.Color pressedColor { get; set; }
        public UnityEngine.Color disabledColor { get; set; }
        public UnityEngine.Color selectedColor { get; set; }
        public float colorMultiplier { get; set; }
    }
}
namespace VRC.SDKBase
{
    public class VRC_EventHandler {
        public enum VrcBroadcastType { Always, Never }
    }
    public static class Networking {
        public static bool IsOwner(UnityEngine.GameObject obj) => false;
        public static void SetOwner(VRCPlayerApi player, UnityEngine.GameObject obj) { }
        public static bool IsMaster => false;
        public static VRCPlayerApi LocalPlayer => null;
    }
    public class VRCPlayerApi : UnityEngine.Object {
        public int playerId;
        public bool isLocal;
        public bool isMaster;
        public VRC_Pickup GetPickupInHand(VRC_Pickup.PickupHand hand) => null;
        public void SetJumpImpulse(float v) { }
        public void SetRunSpeed(float v) { }
        public void SetWalkSpeed(float v) { }
        public void SetStrafeSpeed(float v) { }
        public void SetGravityStrength(float v) { }
        public void UseLegacyLocomotion() { }
        public void SetVoiceGain(float v) { }
        public void SetVoiceDistanceFar(float v) { }
        public void SetVoiceDistanceNear(float v) { }
        public void SetVoiceVolumetricRadius(float v) { }
        public void SetVoiceLowpass(bool v) { }
        public void SetAvatarAudioGain(float v) { }
        public void SetAvatarAudioFarRadius(float v) { }
        public void SetAvatarAudioNearRadius(float v) { }
        public void SetAvatarAudioVolumetricRadius(float v) { }
        public void SetAvatarAudioForceSpatial(bool v) { }
        public void SetAvatarAudioCustomCurve(bool v) { }
        public UnityEngine.Vector3 GetBonePosition(UnityEngine.HumanBodyBones bone) => default;
        public UnityEngine.Quaternion GetBoneRotation(UnityEngine.HumanBodyBones bone) => default;
    }
    public class VRC_Pickup : UnityEngine.Component {
        public enum PickupHand { None, Left, Right }
        // Match the real VRCSDKBase metadata: these are fields, not CLR properties.
        public string InteractionText;
        public string UseText;
        public void Drop() { }
    }
    public class VRCUrl { public string Get() => null; }
    public class VRCStation : UnityEngine.Component { }
    public static class Utilities { public static bool IsValid(UnityEngine.Object obj) => obj != null; }
}
namespace VRC.Udon {
    public class UdonBehaviour : UnityEngine.MonoBehaviour {
        public void SetProgramVariable(string name, object value) { }
        public object GetProgramVariable(string name) => null;
        public void SendCustomEvent(string eventName) { }
        public void SendCustomEventDelayedSeconds(string eventName, float seconds, VRC.Udon.Common.Enums.EventTiming timing = VRC.Udon.Common.Enums.EventTiming.Update) { }
        public void SendCustomEventDelayedFrames(string eventName, int frames, VRC.Udon.Common.Enums.EventTiming timing = VRC.Udon.Common.Enums.EventTiming.Update) { }
        public bool DisableInteractive { get; set; }
    }
}
namespace VRC.Udon.Common.Enums { public enum EventTiming { Update, LateUpdate } }
namespace VRC.Udon.Common.Interfaces { public enum NetworkEventTarget { All, Owner, Others } }
namespace VRC.SDK3.UdonNetworkCalling { public class NetworkCallableAttribute : System.Attribute { } }
namespace VRC.SDK3.Components {
    public class VRCStation : UnityEngine.Component { }
    public class VRCObjectSync : UnityEngine.Component { }
    public class VRCAvatarPedestal : UnityEngine.Component { }
    public class VRCPickup : VRC.SDKBase.VRC_Pickup { }
}
namespace VRC.SDK3.StringLoading {
    public interface IVRCStringDownload {
        string Result { get; }
        VRC.SDKBase.VRCUrl Url { get; }
    }
}
namespace VRC.SDKBase {
    public class VRC_AvatarPedestal : UnityEngine.Component { }
}
namespace VRC.SDK3.Video.Components.Base { public class BaseVRCVideoPlayer : UnityEngine.Component { } }
namespace VRC.SDK3.Video.Components { public class VRCUnityVideoPlayer : VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer { } }
namespace VRC.SDK3.Video.Components.AVPro { public class VRCAVProVideoPlayer : VRC.SDK3.Video.Components.Base.BaseVRCVideoPlayer { } }
namespace TMPro
{
    public class TMP_Text : UnityEngine.UI.Graphic { public string text { get; set; } }
    public class TextMeshProUGUI : TMP_Text { }
    public class TextMeshPro : TMP_Text { }
}
namespace JetBrains.Annotations { public class PublicAPIAttribute : System.Attribute { } }
namespace TestStubs
{
    public interface IToggleable { void Toggle(); }
    public interface IScored { int GetScore(); }
    public class BaseEnemy : UdonSharp.UdonSharpBehaviour
    {
        protected int _hp;
        public void TakeDamage(int amount) { _hp = _hp - amount; }
        public int GetHp() { return _hp; }
    }
    public struct MyVec3
    {
        public float x;
        public float y;
        public float z;
        public MyVec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public struct MyColor
    {
        public float r;
        public float g;
        public float b;
    }
}
namespace VRC.TestStubs
{
    public class DisposableResource : System.IDisposable
    {
        public int Value;
        public void Dispose() { }
    }
}
";

    public static string CompileToUasm(string source)
    {
        var trees = new Microsoft.CodeAnalysis.SyntaxTree[]
        {
            CSharpSyntaxTree.ParseText(Stubs),
            CSharpSyntaxTree.ParseText(source),
        };

        var compilation = CSharpCompilation.Create("TestAssembly", trees, _standardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (diags.Length > 0)
            throw new System.Exception("Compilation errors:\n" + string.Join("\n", diags.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(trees[1]);
        var root = trees[1].GetRoot();
        var classDecl = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .First(c => c is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);
        var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

        var emitter = new UasmEmitter(compilation, classSymbol, externRegistry: RegistryFacts);
        var uasm = emitter.Emit();
        UasmValidator.Validate(
            uasm, emitter.Module.TypeFacts, emitter.CodeGenResult.RepresentationCopies);
        UasmValidator.ValidateHeapConsistency(uasm, emitter.GetHeapSize());
        return uasm;
    }

    public static string CompileToUasm(string source, string className)
    {
        var trees = new Microsoft.CodeAnalysis.SyntaxTree[]
        {
            CSharpSyntaxTree.ParseText(Stubs),
            CSharpSyntaxTree.ParseText(source),
        };

        var compilation = CSharpCompilation.Create("TestAssembly", trees, _standardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (diags.Length > 0)
            throw new System.Exception("Compilation errors:\n" + string.Join("\n", diags.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(trees[1]);
        var root = trees[1].GetRoot();
        var classDecl = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .First(c => c.Identifier.Text == className && c is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);
        var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

        var emitter = new UasmEmitter(compilation, classSymbol, externRegistry: RegistryFacts);
        var uasm = emitter.Emit();
        UasmValidator.Validate(
            uasm, emitter.Module.TypeFacts, emitter.CodeGenResult.RepresentationCopies);
        UasmValidator.ValidateHeapConsistency(uasm, emitter.GetHeapSize());
        return uasm;
    }

    /// <summary>Build a Compilation + resolve the named class WITHOUT emitting — for analysis-only
    /// tests (e.g. CaptureScopeAnalysis) that must run regardless of whether the emit-time guards
    /// would reject the source.</summary>
    public static Microsoft.CodeAnalysis.Compilation BuildCompilation(string source, string className, out INamedTypeSymbol classSymbol)
    {
        var trees = new Microsoft.CodeAnalysis.SyntaxTree[]
        {
            CSharpSyntaxTree.ParseText(Stubs),
            CSharpSyntaxTree.ParseText(source),
        };

        var compilation = CSharpCompilation.Create("TestAssembly", trees, _standardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (diags.Length > 0)
            throw new System.Exception("Compilation errors:\n" + string.Join("\n", diags.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(trees[1]);
        var root = trees[1].GetRoot();
        var classDecl = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .First(c => c.Identifier.Text == className && c is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);
        classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        return compilation;
    }

    /// <summary>CA call-graph rewrite (M0): resolve the edges of the first `calleeName` invocation inside
    /// `callInMethod` of `cls` via the ResolvedEdgeResolver — the per-shape equivalence-gate bridge.</summary>
    public static List<ResolvedTarget> ResolveEdgesForFirstCall(
        string src, string cls, string callInMethod, string calleeName)
    {
        var comp = BuildCompilation(src, cls, out var classSymbol);
        var emitter = new UasmEmitter(comp, classSymbol, externRegistry: RegistryFacts);
        emitter.Emit();
        var op = FindFirstInvocationOp(comp, callInMethod, calleeName);
        return emitter.DebugBuildResolver().ResolveEdges(op).ToList();
    }

    /// <summary>C4 (M5d): the resolver's CallEdge set for the first `calleeName` invocation inside
    /// `callInMethod` of `cls`, OriginalDefinition-projected — the direct-fact successor of the M0
    /// old-vs-new differential (CompareInvocationCallEdges), which became a self-diff once the
    /// classifier core relocated into the resolver. Builds+emits first so VirtualDispatch is seeded
    /// (the CallEdge arm fails loud pre-seed).</summary>
    public static HashSet<IMethodSymbol> ResolveInvocationCallEdges(
        string src, string cls, string callInMethod, string calleeName)
    {
        var comp = BuildCompilation(src, cls, out var classSymbol);
        var emitter = new UasmEmitter(comp, classSymbol, externRegistry: RegistryFacts);
        emitter.Emit();
        var op = FindFirstInvocationOp(comp, callInMethod, calleeName);
        return emitter.DebugBuildResolver().ResolveEdges(op)
            .Where(t => t.Role == TargetRole.CallEdge)
            .Select(t => t.Method.OriginalDefinition)
            .ToHashSet((IEqualityComparer<IMethodSymbol>)SymbolEqualityComparer.Default);
    }

    /// <summary>CA call-graph rewrite (M0 Task 3+): resolve all edges of the first operation anywhere in
    /// `cls`'s method bodies satisfying `match` — the per-op shape gate for the reach arms.</summary>
    public static List<ResolvedTarget> ResolveEdgesForFirstOp(
        string src, string cls, System.Func<Microsoft.CodeAnalysis.IOperation, bool> match)
    {
        var comp = BuildCompilation(src, cls, out var classSymbol);
        var emitter = new UasmEmitter(comp, classSymbol, externRegistry: RegistryFacts);
        emitter.Emit();
        var tree = comp.SyntaxTrees.Last();
        var model = comp.GetSemanticModel(tree);
        foreach (var mdecl in tree.GetRoot().DescendantNodes()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
        {
            var body = model.GetOperation(mdecl);
            if (body == null) continue;
            foreach (var o in DescendantOps(body))
                if (match(o)) return emitter.DebugBuildResolver().ResolveEdges(o).ToList();
        }
        throw new System.Exception("no matching op found in " + cls);
    }

    /// <summary>CA rewrite (M5 prerequisite): the minted user classes the resolver reports for the first
    /// object-creation op anywhere in `cls`'s methods.</summary>
    public static List<INamedTypeSymbol> ResolveMintedTypesForFirstNew(string src, string cls)
    {
        var comp = BuildCompilation(src, cls, out var classSymbol);
        var emitter = new UasmEmitter(comp, classSymbol, externRegistry: RegistryFacts);
        emitter.Emit();
        var tree = comp.SyntaxTrees.Last();
        var model = comp.GetSemanticModel(tree);
        foreach (var mdecl in tree.GetRoot().DescendantNodes()
                     .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
        {
            var body = model.GetOperation(mdecl);
            if (body == null) continue;
            foreach (var o in DescendantOps(body))
                if (o is Microsoft.CodeAnalysis.Operations.IObjectCreationOperation)
                    return emitter.DebugBuildResolver().ResolveMintedTypes(o).ToList();
        }
        throw new System.Exception("no object-creation op found in " + cls);
    }

    static Microsoft.CodeAnalysis.IOperation FindFirstInvocationOp(
        Microsoft.CodeAnalysis.Compilation comp, string callInMethod, string calleeName)
    {
        var tree = comp.SyntaxTrees.Last();
        var model = comp.GetSemanticModel(tree);
        var methodDecl = tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == callInMethod);
        var body = model.GetOperation(methodDecl);
        return DescendantOps(body)
            .OfType<Microsoft.CodeAnalysis.Operations.IInvocationOperation>()
            .First(i => i.TargetMethod.Name == calleeName);
    }

    static IEnumerable<Microsoft.CodeAnalysis.IOperation> DescendantOps(Microsoft.CodeAnalysis.IOperation op)
    {
        foreach (var c in op.ChildOps())
        {
            yield return c;
            foreach (var d in DescendantOps(c)) yield return d;
        }
    }

    public static string CompileToUasm(string source, string className, out UasmEmitter outEmitter)
    {
        var trees = new Microsoft.CodeAnalysis.SyntaxTree[]
        {
            CSharpSyntaxTree.ParseText(Stubs),
            CSharpSyntaxTree.ParseText(source),
        };

        var compilation = CSharpCompilation.Create("TestAssembly", trees, _standardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (diags.Length > 0)
            throw new System.Exception("Compilation errors:\n" + string.Join("\n", diags.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(trees[1]);
        var root = trees[1].GetRoot();
        var classDecl = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .First(c => c.Identifier.Text == className && c is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);
        var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

        var emitter = new UasmEmitter(compilation, classSymbol, externRegistry: RegistryFacts);
        var uasm = emitter.Emit();
        UasmValidator.Validate(
            uasm, emitter.Module.TypeFacts, emitter.CodeGenResult.RepresentationCopies);
        UasmValidator.ValidateHeapConsistency(uasm, emitter.GetHeapSize());
        outEmitter = emitter;
        return uasm;
    }

    public static string CompileToUasm(string[] sources, string className)
    {
        var trees = new List<Microsoft.CodeAnalysis.SyntaxTree> { CSharpSyntaxTree.ParseText(Stubs) };
        foreach (var src in sources)
            trees.Add(CSharpSyntaxTree.ParseText(src));

        var compilation = CSharpCompilation.Create("TestAssembly", trees, _standardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (diags.Length > 0)
            throw new System.Exception("Compilation errors:\n" + string.Join("\n", diags.Select(d => d.ToString())));

        // Search all source trees (skip stubs tree at index 0)
        Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax classDecl = null;
        SemanticModel model = null;
        for (int i = 1; i < trees.Count; i++)
        {
            var root = trees[i].GetRoot();
            classDecl = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == className && c is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);
            if (classDecl != null)
            {
                model = compilation.GetSemanticModel(trees[i]);
                break;
            }
        }
        if (classDecl == null)
            throw new System.Exception($"Class '{className}' not found in any source");

        var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

        var emitter = new UasmEmitter(compilation, classSymbol, externRegistry: RegistryFacts);
        var uasm = emitter.Emit();
        UasmValidator.Validate(
            uasm, emitter.Module.TypeFacts, emitter.CodeGenResult.RepresentationCopies);
        UasmValidator.ValidateHeapConsistency(uasm, emitter.GetHeapSize());
        return uasm;
    }

    public static (string uasm, List<(string Id, string UdonType, object Value)> consts) CompileWithConsts(string source, string className)
    {
        var trees = new Microsoft.CodeAnalysis.SyntaxTree[]
        {
            CSharpSyntaxTree.ParseText(Stubs),
            CSharpSyntaxTree.ParseText(source),
        };

        var compilation = CSharpCompilation.Create("TestAssembly", trees, _standardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (diags.Length > 0)
            throw new System.Exception("Compilation errors:\n" + string.Join("\n", diags.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(trees[1]);
        var root = trees[1].GetRoot();
        var classDecl = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .First(c => c.Identifier.Text == className && c is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax);
        var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;

        var emitter = new UasmEmitter(compilation, classSymbol, externRegistry: RegistryFacts);
        var uasm = emitter.Emit();
        UasmValidator.Validate(
            uasm, emitter.Module.TypeFacts, emitter.CodeGenResult.RepresentationCopies);
        UasmValidator.ValidateHeapConsistency(uasm, emitter.GetHeapSize());
        return (uasm, emitter.CodeGenResult.Constants);
    }
}
