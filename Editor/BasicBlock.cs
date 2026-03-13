using System.Collections.Generic;

public class BasicBlock
{
    public int Id;
    public List<Inst> Instructions = new();
    public List<BasicBlock> Predecessors = new();
    public List<BasicBlock> Successors = new();
    public BitSet Gen, Kill, LiveIn, LiveOut;
}
