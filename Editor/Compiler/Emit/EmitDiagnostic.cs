public struct EmitDiagnostic
{
    public string Severity; // "Warning" or "Error"
    public string Message;
    public string FilePath;
    public int Line;
    public int Character;
}
