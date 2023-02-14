﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace luadec.IR
{
    /// <summary>
    /// A Lua function. A function contains a CFG, a list of instructions, and child functions used for closures
    /// </summary>
    public class Function
    {
        private List<Identifier> Parameters;
        private List<Function> Closures;
        private List<IInstruction> Instructions;

        private Dictionary<uint, Label> Labels;

        /// <summary>
        /// Set to true when this is converted to a CFG
        /// </summary>
        private bool IsControlFlowGraph = false;

        /// <summary>
        /// When the CFG has been converted to an AST
        /// </summary>
        private bool IsAST = false;

        /// <summary>
        /// The first basic block in which control flow enters upon the function being called
        /// </summary>
        private CFG.BasicBlock BeginBlock;

        /// <summary>
        /// The final (empty) basic block that is the successor to the end of the function and any blocks that end with a return instruction
        /// </summary>
        private CFG.BasicBlock EndBlock;

        /// <summary>
        /// List of all the blocks for some analyses
        /// </summary>
        private List<CFG.BasicBlock> BlockList;

        /// <summary>
        /// Identifiers that are used in more than one basic block
        /// </summary>
        private HashSet<Identifier> GlobalIdentifiers;

        /// <summary>
        /// All the renamed SSA variables
        /// </summary>
        private HashSet<Identifier> SSAVariables;

        private static int IndentLevel = 0;

        public static int DebugIDCounter = 0;
        public int DebugID = 0;

        public List<LuaFile.Local> ArgumentNames = null;

        public bool IsVarargs = false;

        /// <summary>
        /// Number of upvalues this function uses
        /// </summary>
        public int UpvalCount = 0;

        /// <summary>
        /// For each upvalue in Lua 5.3, the register in the parent its bound to
        /// </summary>
        public List<int> UpvalueRegisterBinding = new List<int>();

        /// <summary>
        /// For each upvalue in Lua 5.3, if the upvalue exists on the stack
        /// </summary>
        public List<bool> UpvalueIsStackBinding = new List<bool>();

        /// <summary>
        /// Upvalue binding symbold from parent closure
        /// </summary>
        public List<Identifier> UpvalueBindings = new List<Identifier>();

        public Function()
        {
            Parameters = new List<Identifier>();
            Closures = new List<Function>();
            Instructions = new List<IInstruction>();
            Labels = new Dictionary<uint, Label>();
            BlockList = new List<CFG.BasicBlock>();
            GlobalIdentifiers = new HashSet<Identifier>();
            SSAVariables = new HashSet<Identifier>();
            DebugID = DebugIDCounter;
            DebugIDCounter++;
        }

        public void AddInstruction(IInstruction inst)
        {
            Instructions.Add(inst);
        }

        public void AddClosure(Function fun)
        {
            Closures.Add(fun);
        }

        public Function LookupClosure(uint i)
        {
            return Closures[(int)i];
        }

        public void SetParameters(List<Identifier> parameters)
        {
            Parameters = parameters;
        }

        public Label GetLabel(uint pc)
        {
            if (Labels.ContainsKey(pc))
            {
                Labels[pc].UsageCount++;
                return Labels[pc];
            }
            var label = new Label();
            label.OpLocation = (int)pc;
            label.UsageCount = 1;
            Labels.Add(pc, label);
            return label;
        }

        /// <summary>
        /// Call after a function's IR is generated to actually insert the labels into the instruction stream
        /// </summary>
        public void ApplyLabels()
        {
            // O(n^2) naive algorithm so sue me
            foreach (var l in Labels)
            {
                for (int i = 0; i < Instructions.Count(); i++)
                {
                    if (Instructions[i].OpLocation == l.Key)
                    {
                        Instructions.Insert(i, l.Value);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Removes data instructions from the IR, which from what I can tell doesn't help with decompilation
        /// </summary>
        public void ClearDataInstructions()
        {
            for (int i = Instructions.Count() - 1; i > 0; i--)
            {
                if (Instructions[i] is Data d1)
                {
                    if (Instructions[i - 1] is Data d2)
                    {
                        d2.Locals = d1.Locals;
                    }
                    else if (Instructions[i - 1] is Assignment a)
                    {
                        a.LocalAssignments = d1.Locals;
                    }
                    Instructions.RemoveAt(i);
                    i++;
                }
            }
        }

        /// <summary>
        /// Super simple analysis that eliminates assignments of the form:
        /// REGA = REGA
        /// 
        /// These are often generated by the TEST instruction and elimination of these simplifies things for future passes
        /// </summary>
        public void EliminateRedundantAssignments()
        {
            for (int i = 0; i < Instructions.Count(); i++)
            {
                // If we encounter a closure we must skip instructions equal to the number of upvalues, as the assignments that follow are
                // critical for upvalue binding analysis
                if (Instructions[i] is Assignment a && a.Right is Closure c)
                {
                    i += c.Function.UpvalCount;
                }
                else if (Instructions[i] is Assignment assn && assn.Left.Count() == 1 && !assn.Left[0].HasIndex)
                {
                    if (assn.Right is IdentifierReference reference && !reference.HasIndex && assn.Left[0].Identifier == reference.Identifier)
                    {
                        Instructions.RemoveAt(i);
                        i--;
                    }
                }
            }
        }
        
        /// <summary>
        /// Simple analysis pass to recognize lua conditional jumping patterns and merge them into a single instruction
        /// </summary>
        public void MergeConditionalJumps()
        {
            // Lua conditional jumps often follow this pattern when naively translated into the IR:
            //   if REGA == b then goto Label_1:
            //   goto Label_2:
            //   Label_1:
            //   ...
            //
            // This pass recognizes and simplifies this into:
            //   if REGA ~= b then goto Label_2:
            //   ...
            //
            // This will greatly simplify the generated control flow graph, so this is done first
            // This algorithm is run until convergence
            int instanceCounts = 1;
            //while (instanceCounts > 0)
            {
                instanceCounts = 0;
                for (int i = 0; i < Instructions.Count() - 2; i++)
                {
                    // Pattern match the prerequisites
                    if (Instructions[i] is Jump jmp1 && jmp1.Conditional &&
                        Instructions[i + 1] is Jump jmp2 && !jmp2.Conditional &&
                        Instructions[i + 2] is Label shortLabel && jmp1.Dest == shortLabel)
                    {
                        // flip the condition and change the destination to the far jump. Then remove the following goto and label
                        if (jmp1.Condition is BinOp op)
                        {
                            op.NegateCondition();
                            jmp1.Dest.UsageCount--;
                            Instructions.RemoveRange(i + 1, jmp1.Dest.UsageCount <= 0 ? 2 : 1);
                            jmp1.Dest = jmp2.Dest;
                            instanceCounts++;
                        }
                        else if ((jmp1.Condition is UnaryOp op2 && op2.Operation == UnaryOp.OperationType.OpNot) || jmp1.Condition is IdentifierReference)
                        {
                            jmp1.Dest.UsageCount--;
                            Instructions.RemoveRange(i + 1, jmp1.Dest.UsageCount <= 0 ? 2 : 1);
                            jmp1.Dest = jmp2.Dest;
                            instanceCounts++;
                        }
                        else
                        {
                            throw new Exception("Recognized jump pattern does not use a binary op conditional");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// HKS as an optimization seems to optimize the following:
        ///     local a, b, c, d = false
        /// as two instructions: LOADBOOL followed by LOADNIL. It seems that
        /// LOADNIL can recognize when it's preceded by LOADBOOL and load a bool
        /// instead of a nil into the register. This pass recognizes this idiom and
        /// merges them back together. This only works when the bool is false as it's unknown
        /// if it does this for true as well.
        /// </summary>
        public void MergeMultiBoolAssignment()
        {
            for (int i = 0; i < Instructions.Count() - 2; i++)
            {
                if (Instructions[i] is Assignment a1 && a1.Left.Count() > 0 && !a1.Left[0].HasIndex &&
                    a1.Right is Constant c && c.ConstType == Constant.ConstantType.ConstBool && !c.Boolean &&
                    Instructions[i + 1] is Assignment a2 && a2.Left.Count() > 0 && !a2.Left[0].HasIndex &&
                    a2.Right is Constant c2 && c2.ConstType == Constant.ConstantType.ConstNil)
                {
                    a1.Left.AddRange(a2.Left);
                    a1.MergeLocalAssignments(a2);
                    Instructions.RemoveAt(i + 1);
                }
            }
        }

        public void MergeConditionalAssignments()
        {
            // Sometimes a conditional assignment is generated in lua to implement something like:
            // local var = somefunction() == true
            //
            // This would generate the following IR:
            // if REGA == 1234 else goto label_1
            // REGB = false
            // goto label_2
            // label_1:
            // REGB = true
            // label_2:
            // ...
            // This pattern matches such a case and replaces it with just:
            // REGB = REGA ~= 1234
            for (int i = 0; i < Instructions.Count() - 6; i++)
            {
                // Big pattern match
                if (Instructions[i] is Jump jmp && jmp.Conditional &&
                    Instructions[i + 1] is Assignment asscond1 && asscond1.Left.Count() == 1 && asscond1.Left[0] is IdentifierReference assignee && 
                    asscond1.Right is Constant c1 && c1.ConstType == Constant.ConstantType.ConstBool && !c1.Boolean &&
                    Instructions[i + 2] is Jump jmp2 && !jmp2.Conditional &&
                    Instructions[i + 3] is Label label1 && label1 == jmp.Dest &&
                    Instructions[i + 4] is Assignment asscond2 && asscond2.Left.Count() == 1 && asscond2.Left[0] is IdentifierReference assignee2 && 
                    assignee.Identifier == assignee2.Identifier && asscond2.Right is Constant c2 && c2.ConstType == Constant.ConstantType.ConstBool && c2.Boolean &&
                    Instructions[i + 5] is Label label2 && label2 == jmp2.Dest)
                {
                    if (jmp.Condition is BinOp bop)
                    {
                        bop.NegateCondition();
                    }
                    var newassn = new Assignment(assignee, jmp.Condition);
                    Instructions[i] = newassn;
                    Instructions.RemoveRange(i + 1, 4); // Don't remove the final label as it can be a jump destination sometimes
                }
            }
        }

        public void PeepholeOptimize()
        {
            // Optimizes out jumps to jumps, and deletes labels too if they become unused as a result
            for (int i = 0; i < Instructions.Count(); i++)
            {
                if (Instructions[i] is Jump jmp1)
                {
                    IInstruction dest = Instructions[Instructions.IndexOf(jmp1.Dest) + 1];
                    while (dest is Jump jmp2 && !jmp2.Conditional)
                    {
                        jmp1.Dest.UsageCount--;
                        if (jmp1.Dest.UsageCount <= 0)
                        {
                            Instructions.Remove(jmp1.Dest);
                        }
                        jmp1.Dest = jmp2.Dest;
                        dest = Instructions[Instructions.IndexOf(jmp1.Dest) + 1];
                    }
                }
            }
        }

        // Validates control flow integrity of the function (every jump destination label exists)
        public void CheckControlFlowIntegrity()
        {
            for (int i = 0; i < Instructions.Count(); i++)
            {
                if (Instructions[i] is Jump jmp)
                {
                    if (Instructions.IndexOf(jmp.Dest) == -1)
                    {
                        throw new Exception("Control flow is corrupted");
                    }
                }
            }
        }

        /// <summary>
        /// Transforms the current function into a control flow graph. This will break the function as a linear set of instructions
        /// </summary>
        public void ConstructControlFlowGraph()
        {
            IsControlFlowGraph = true;

            // Create the begin and end basic blocks
            CFG.BasicBlock.ResetCounter();
            BeginBlock = new CFG.BasicBlock();
            EndBlock = new CFG.BasicBlock();
            BlockList.Add(BeginBlock);

            // These are used to connect jmps to their destinations later
            var labelBasicBlockMap = new Dictionary<Label, CFG.BasicBlock>();

            bool cullNextReturn = false;

            // First pass: Build all the basic blocks using labels, jmps, and rets as boundries
            var currentBlock = BeginBlock;
            for (int i = 0; i < Instructions.Count(); i++)
            {
                // Unconditional jumps just start a new basic block
                if (Instructions[i] is Jump jmp && !jmp.Conditional)
                {
                    currentBlock.Instructions.Add(jmp);
                    jmp.Block = currentBlock;
                    currentBlock = new CFG.BasicBlock();
                    BlockList.Add(currentBlock);
                    if (i + 1 < Instructions.Count() && Instructions[i+1] is Label l)
                    {
                        labelBasicBlockMap.Add(l, currentBlock);
                        i++;
                    }
                }
                // Conditional jumps has the following block as a successor
                else if (Instructions[i] is Jump jmp2 && jmp2.Conditional)
                {
                    currentBlock.Instructions.Add(jmp2);
                    jmp2.Block = currentBlock;
                    var newBlock = new CFG.BasicBlock();
                    currentBlock.Successors.Add(newBlock);
                    newBlock.Predecessors.Add(currentBlock);
                    currentBlock = newBlock;
                    BlockList.Add(currentBlock);
                    if (i + 1 < Instructions.Count() && Instructions[i + 1] is Label l)
                    {
                        if (l == jmp2.Dest)
                        {
                            // Empty if statement. Generate a dummy block so the true block and else block are different
                            var newblock2 = new CFG.BasicBlock();
                            currentBlock.Instructions.Add(new Jump(l));
                            currentBlock = newblock2;
                            BlockList.Add(currentBlock);
                        }
                        labelBasicBlockMap.Add(l, currentBlock);
                        i++;
                    }
                }
                // Returns simply go directly to the end block, and starts a new basic block if not at the end
                else if (AnalysisOpts.AnalyzeReturns && Instructions[i] is Return ret)
                {
                    if (!ret.IsImplicit || i + 1 == Instructions.Count())
                    {
                        currentBlock.Instructions.Add(ret);
                        ret.Block = currentBlock;

                        currentBlock.Successors.Add(EndBlock);
                        EndBlock.Predecessors.Add(currentBlock);
                        if (i + 1 < Instructions.Count())
                        {
                            currentBlock = new CFG.BasicBlock();
                            BlockList.Add(currentBlock);
                            if (Instructions[i + 1] is Label l)
                            {
                                labelBasicBlockMap.Add(l, currentBlock);
                                i++;
                            }
                        }
                    }
                    else
                    {
                        Instructions.RemoveAt(i);
                        i--;
                    }
                }
                // Alternate return analysis for lua 5.0
                else if (!AnalysisOpts.AnalyzeReturns && Instructions[i] is Return ret2)
                {
                    // If a tailcall was done, an extra return that's not needed will always be generated by the Lua 5.0 compiler
                    if (!cullNextReturn)
                    {
                        if (ret2.IsTailReturn)
                        {
                            cullNextReturn = true;
                        }
                        currentBlock.Instructions.Add(ret2);
                        ret2.Block = currentBlock;
                    }
                    else
                    {
                        cullNextReturn = false;
                    }
                }
                // Other labels just start a new fallthrough basic block
                else if (Instructions[i] is Label l2)
                {
                    var newBlock = new CFG.BasicBlock();
                    currentBlock.Successors.Add(newBlock);
                    newBlock.Predecessors.Add(currentBlock);
                    currentBlock = newBlock;
                    BlockList.Add(currentBlock);
                    labelBasicBlockMap.Add(l2, currentBlock);
                }
                // Otherwise add instruction to the block
                else
                {
                    currentBlock.Instructions.Add(Instructions[i]);
                    Instructions[i].Block = currentBlock;
                }
            }

            // Second pass: Connect jumps to their basic blocks
            for (int b = 0; b < BlockList.Count(); b++)
            {
                if (BlockList[b].Instructions.Count() > 0 && BlockList[b].Instructions.Last() is Jump jmp)
                {
                    BlockList[b].Successors.Add(labelBasicBlockMap[jmp.Dest]);
                    labelBasicBlockMap[jmp.Dest].Predecessors.Add(BlockList[b]);
                    jmp.BBDest = labelBasicBlockMap[jmp.Dest];
                }
            }

            // Third pass: Remove empty blocks (redundant returns)
            for (int b = 0; b < BlockList.Count(); b++)
            {
                if (BlockList[b].Instructions.Count > 0)
                {
                    continue;
                }
                foreach (var pred in BlockList[b].Predecessors)
                {
                    pred.Successors.Remove(BlockList[b]);
                    pred.Successors.Add(BlockList[b].Successors.FirstOrDefault() ?? EndBlock);
                }
                foreach (var succ in BlockList[b].Successors)
                {
                    succ.Predecessors.Remove(BlockList[b]);
                    succ.Predecessors.AddRange(BlockList[b].Predecessors);
                }
                BlockList.RemoveAt(b);
                b--;
            }

            // Fourth pass: Remove unreachable blocks
            for (int b = 0; b < BlockList.Count(); b++)
            {
                // Begin block has no predecessors but shouldn't be removed because :)
                if (BlockList[b] == BeginBlock || BlockList[b].Predecessors.Count() != 0)
                {
                    continue;
                }
                foreach (var succ in BlockList[b].Successors)
                {
                    succ.Predecessors.Remove(BlockList[b]);
                }
                // Remove instructions from deleted blocks
                Instructions.RemoveAll(inst => inst.Block == BlockList[b]);
                BlockList.RemoveAt(b);
                b--;
            }

            // Fifth pass: Merge blocks that have a single successor and that successor has a single predecessor
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int b = 0; b < BlockList.Count(); b++)
                {
                    if (BlockList[b].Successors.Count() == 1 && BlockList[b].Successors[0].Predecessors.Count() == 1 &&
                        (BlockList[b].Instructions.Last() is Jump || (b + 1 < BlockList.Count() && BlockList[b].Successors[0] == BlockList[b + 1])))
                    {
                        var curr = BlockList[b];
                        var succ = BlockList[b].Successors[0];
                        if (BlockList[b].Instructions.Last() is Jump)
                        {
                            curr.Instructions.RemoveAt(curr.Instructions.Count() - 1);
                        }
                        foreach (var inst in succ.Instructions)
                        {
                            inst.Block = curr;
                        }
                        curr.Instructions.AddRange(succ.Instructions);
                        curr.Successors = succ.Successors;
                        foreach (var s in succ.Successors)
                        {
                            for (int p = 0; p < s.Predecessors.Count(); p++)
                            {
                                if (s.Predecessors[p] == succ)
                                {
                                    s.Predecessors[p] = curr;
                                }
                            }
                        }
                        BlockList.Remove(succ);
                        b = Math.Max(0, b - 2);
                        changed = true;
                    }
                }
            }

            // Dangling no successor blocks should go to the end block (implicit return)
            for (int b = 0; b < BlockList.Count(); b++)
            {
                if (BlockList[b] == EndBlock)
                {
                    continue;
                }
                if (BlockList[b].Successors.Count() == 0)
                {
                    BlockList[b].Successors.Add(EndBlock);
                }
            }

            // Conditionals that got optimized into an unconditional jump should have their conditions cleared
            foreach (var inst in Instructions)
            {
                if (inst.Block?.Successors.Count == 1 && inst is Jump jump)
                {
                    jump.Condition = null;
                    jump.Conditional = false;
                }
            }

            BlockList.Add(EndBlock);
        }

        /// <summary>
        /// Resolves function calls that have 0 as its "b", which means the actual arguments are determined by a
        /// previous function with an indefinite return count being the last argument
        /// </summary>
        /// <param name="table"></param>
        public void ResolveIndeterminantArguments(SymbolTable table)
        {
            Console.Write(string.Join(", ", Parameters.Select(p => $"{p.VType.ToString()} {p.Name}")));
            Console.WriteLine(")");
            // This analysis should not need any intrablock analysis
            foreach (var b in BlockList)
            {
                Console.WriteLine($"    {b.GetName()}:");

                Identifier lastIndeterminantRet = null;
                foreach (var i in b.Instructions)
                {
                    if (i.ToString().Length > 0)
                        Console.WriteLine($"        {i.ToString()}");

                    if (i is Assignment a2 && a2.Right is FunctionCall fc2 && fc2.IsIndeterminantArgumentCount)
                    {
                        if (lastIndeterminantRet == null)
                        {
                            throw new Exception("Error: Indeterminant argument function call without preceding indeterminant return function call");
                        }
                        for (uint r = fc2.BeginArg; r <= lastIndeterminantRet.Regnum; r++)
                        {
                            fc2.Args.Add(new IdentifierReference(table.GetRegister(r)));
                        }
                        lastIndeterminantRet = null;
                    }
                    if (i is Return ret && ret.IsIndeterminantReturnCount)
                    {
                        if (lastIndeterminantRet == null)
                        {
                            throw new Exception("Error: Indeterminant return without preceding indeterminant return function call");
                        }
                        for (uint r = ret.BeginRet; r <= lastIndeterminantRet.Regnum; r++)
                        {
                            ret.ReturnExpressions.Add(new IdentifierReference(table.GetRegister(r)));
                        }
                    }
                    if (i is Assignment a && a.Left.Count() == 1 && !a.Left[0].HasIndex && a.Right is FunctionCall fc && fc.IsIndeterminantReturnCount)
                    {
                        lastIndeterminantRet = a.Left[0].Identifier;
                    }
                }
            }

            Console.WriteLine("end");
        }

        public void ResolveVarargListAssignment()
        {
            for (int i = 0; i < Instructions.Count() - 2; i++)
            {
                if (Instructions[i] is Assignment a1 && a1.Right is InitializerList l1 && l1.Exprs.Count() == 0 &&
                    Instructions[i + 1] is Assignment a2 && a2.IsIndeterminantVararg && a1.VarargAssignmentReg == (a2.VarargAssignmentReg - 1) &&
                    Instructions[i + 2] is Assignment a3 && a3.IsIndeterminantVararg && a3.VarargAssignmentReg == a1.VarargAssignmentReg)
                {
                    l1.Exprs.Add(new IR.Constant(Constant.ConstantType.ConstVarargs, -1));
                    a1.LocalAssignments = a3.LocalAssignments;
                    Instructions.RemoveRange(i + 1, 2);
                }
            }
        }

        /// <summary>
        /// Finishes implementing the last part of the Lua 5.1 FORLOOP op. I.e. in the block that follows the loop head that doesn't
        /// break the loop insert the following IR: R(A+3) := R(A)
        /// </summary>
        public void CompleteLua51Loops()
        {
            foreach (var b in BlockList)
            {
                if (b.Instructions.Count() > 0 && b.Instructions.Last() is Jump jmp && jmp.PostTakenAssignment != null)
                {
                    b.Successors[1].Instructions.Insert(0, jmp.PostTakenAssignment);
                    jmp.PostTakenAssignment.PropagateAlways = true;
                    jmp.PostTakenAssignment.Block = b.Successors[1];
                    jmp.PostTakenAssignment = null;
                }
            }
        }

        /// <summary>
        /// Computes the dominance sets for all the nodes as well as the dominance tree
        /// </summary>
        private void ComputeDominance()
        {
            // Start block only has itself in dominace set
            BeginBlock.Dominance.Clear();
            BeginBlock.DominanceTreeSuccessors.Clear();
            BeginBlock.Dominance.Add(BeginBlock);

            // All blocks but the start have everything dominate them to begin the algorithm
            for (int i = 1; i < BlockList.Count(); i++)
            {
                BlockList[i].Dominance = new HashSet<CFG.BasicBlock>(BlockList);
                BlockList[i].DominanceTreeSuccessors.Clear();
            }

            // Iterative solver of dominance data flow equation
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 1; i < BlockList.Count(); i++)
                {
                    var temp = new HashSet<CFG.BasicBlock>(BlockList);
                    foreach (var p in BlockList[i].Predecessors)
                    {
                        temp.IntersectWith(p.Dominance);
                    }
                    temp.UnionWith(new[] { BlockList[i] });
                    if (!temp.SetEquals(BlockList[i].Dominance))
                    {
                        BlockList[i].Dominance = temp;
                        changed = true;
                    }
                }
            }

            // Compute the immediate dominator
            for (int i = 1; i < BlockList.Count(); i++)
            {
                BlockList[i].ComputeImmediateDominator();
            }
        }

        private void ComputeDominanceFrontier()
        {
            for (int i = 0; i < BlockList.Count(); i++)
            {
                if (BlockList[i].Predecessors.Count() > 1)
                {
                    foreach (var p in BlockList[i].Predecessors)
                    {
                        var runner = p;
                        while (runner != BlockList[i].ImmediateDominator)
                        {
                            runner.DominanceFrontier.UnionWith(new[] { BlockList[i] });
                            runner = runner.ImmediateDominator;
                        }
                    }
                }
            }
        }

        private void ComputeGlobalLiveness(HashSet<Identifier> allRegisters)
        {
            foreach (var b in BlockList)
            {
                b.KilledIdentifiers.Clear();
                b.UpwardExposedIdentifiers.Clear();
                b.LiveOut.Clear();
                GlobalIdentifiers.UnionWith(b.ComputeKilledAndUpwardExposed());
            }

            // Compute live out for each block iteratively
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var b in BlockList)
                {
                    var temp = new HashSet<Identifier>();
                    foreach (var succ in b.Successors)
                    {
                        var equation = new HashSet<Identifier>(allRegisters);
                        foreach (var kill in succ.KilledIdentifiers)
                        {
                            equation.Remove(kill);
                        }
                        equation.IntersectWith(succ.LiveOut);
                        equation.UnionWith(succ.UpwardExposedIdentifiers);
                        temp.UnionWith(equation);
                    }
                    if (!b.LiveOut.SetEquals(temp))
                    {
                        b.LiveOut = temp;
                        changed = true;
                    }
                }
            }
        }

        public void ConvertToSSA(HashSet<Identifier> allRegisters)
        {
            allRegisters.UnionWith(new HashSet<Identifier>(Parameters));
            ComputeDominance();
            ComputeDominanceFrontier();
            ComputeGlobalLiveness(allRegisters);

            // Now insert all the needed phi functions
            foreach (var g in GlobalIdentifiers)
            {
                var work = new Queue<CFG.BasicBlock>();
                var visitedSet = new HashSet<CFG.BasicBlock>();
                foreach (var b in BlockList)
                {
                    if (b != EndBlock && b.KilledIdentifiers.Contains(g))
                    {
                        work.Enqueue(b);
                        visitedSet.Add(b);
                    }
                }
                while (work.Count() > 0)
                {
                    var b = work.Dequeue();
                    foreach (var d in b.DominanceFrontier)
                    {
                        if (d != EndBlock && !d.PhiFunctions.ContainsKey(g))
                        {
                            // Heuristic: if the block is just a single return, we don't need phi functions
                            if (d.Instructions.First() is Return r && r.ReturnExpressions.Count() == 0)
                            {
                                continue;
                            }

                            var phiargs = new List<Identifier>();
                            for (int i = 0; i < d.Predecessors.Count(); i++)
                            {
                                phiargs.Add(g);
                            }
                            d.PhiFunctions.Add(g, new PhiFunction(g, phiargs));
                            //if (!visitedSet.Contains(d))
                            //{
                                work.Enqueue(d);
                                visitedSet.Add(d);
                            //}
                        }
                    }
                }
            }



            // Prepare for renaming
            var Counters = new Dictionary<Identifier, int>();
            var Stacks = new Dictionary<Identifier, Stack<Identifier>>();
            foreach (var reg in allRegisters)
            {
                Counters.Add(reg, 0);
                Stacks.Add(reg, new Stack<Identifier>());
            }

            // Creates a new identifier based on an original identifier
            Identifier NewName(Identifier orig)
            {
                var newName = new Identifier();
                newName.Name = orig.Name + $@"_{Counters[orig]}";
                newName.IType = Identifier.IdentifierType.Register;
                newName.OriginalIdentifier = orig;
                newName.IsClosureBound = orig.IsClosureBound;
                Stacks[orig].Push(newName);
                Counters[orig]++;
                SSAVariables.Add(newName);
                return newName;
            }

            void RenameBlock(CFG.BasicBlock b)
            {
                // Rewrite phi function definitions
                foreach (var phi in b.PhiFunctions)
                {
                    phi.Value.RenameDefines(phi.Key, NewName(phi.Key));
                }

                // Rename other instructions
                foreach (var inst in b.Instructions)
                {
                    foreach (var use in inst.GetUses(true))
                    {
                        if (use.IsClosureBound)
                        {
                            continue;
                        }
                        if (Stacks[use].Count != 0)
                        {
                            inst.RenameUses(use, Stacks[use].Peek());
                        }
                    }
                    foreach (var def in inst.GetDefines(true))
                    {
                        if (def.IsClosureBound)
                        {
                            continue;
                        }
                        inst.RenameDefines(def, NewName(def));
                    }
                }
                
                // Rename successor phi functions
                foreach (var succ in b.Successors)
                {
                    if (succ != EndBlock)
                    {
                        var index = succ.Predecessors.IndexOf(b);
                        foreach (var phi in succ.PhiFunctions)
                        {
                            if (Stacks[phi.Value.Right[index]].Count() > 0)
                            {
                                phi.Value.Right[index] = Stacks[phi.Value.Right[index]].Peek();
                                phi.Value.Right[index].UseCount++;
                            }
                            else
                            {
                                // Sometimes a phi function is forced when one of the predecessor paths don't actually define the register.
                                // These phi functions are usually not needed and optimized out in a later pass, so we set it to null to detect
                                // errors in case the phi function result is actually used.
                                phi.Value.Right[index] = null;
                            }
                        }
                    }
                }
                
                // Rename successors in the dominator tree
                foreach (var succ in b.DominanceTreeSuccessors)
                {
                    if (succ != EndBlock)
                    {
                        RenameBlock(succ);
                    }

                    // Add to the scope killed set based on the domtree successor's killed and scope killed
                    foreach (var killed in succ.KilledIdentifiers)
                    {
                        if (killed.IType == Identifier.IdentifierType.Register)
                        {
                            b.ScopeKilled.Add(killed.Regnum);
                        }
                        b.ScopeKilled.UnionWith(succ.ScopeKilled);
                    }
                    
                }

                // Pop off anything we pushed
                foreach (var phi in b.PhiFunctions)
                {
                    Stacks[phi.Value.Left.OriginalIdentifier].Pop();
                }
                foreach (var inst in b.Instructions)
                {
                    foreach (var def in inst.GetDefines(true))
                    {
                        if (def.IsClosureBound)
                        {
                            continue;
                        }
                        Stacks[def.OriginalIdentifier].Pop();
                    }
                }
            }

            // Rename the arguments first
            for (int i = 0; i < Parameters.Count(); i++)
            {
                Parameters[i] = NewName(Parameters[i]);
            }

            // Rename everything else recursively
            RenameBlock(BeginBlock);
        }

        // Detect the upvalue bindings for the child closures for Lua 5.0
        public void RegisterClosureUpvalues50()
        {
            foreach (var b in BlockList)
            {
                for (int i = 0; i < b.Instructions.Count(); i++)
                {
                    // Recognize a closure instruction
                    if (b.Instructions[i] is Assignment a && a.Right is Closure c)
                    {
                        // Fetch the closure bindings from the following instructions
                        for (int j = 0; j < c.Function.UpvalCount; j++)
                        {
                            if (b.Instructions[i + 1] is Assignment ca && 
                                ca.Left.Count == 1 && 
                                ca.Left[0].Identifier.Regnum == 0 &&
                                ca.Right is IdentifierReference ir &&
                                ir.Identifier.IType == Identifier.IdentifierType.Register)
                            {
                                c.Function.UpvalueBindings.Add(ir.Identifier);
                                ir.Identifier.IsClosureBound = true;
                                b.Instructions.RemoveAt(i + 1);
                            }
                            else
                            {
                                throw new Exception("Unrecognized upvalue binding pattern following closure");
                            }
                        }
                    }
                }
            }
        }

        // Detect upvalue bindings for child closures in Lua 5.3
        public void RegisterClosureUpvalues53(HashSet<Identifier> allRegisters)
        {
            foreach (var f in Closures)
            {
                for (int i = 0; i < f.UpvalCount; i++)
                {
                    if (f.UpvalueIsStackBinding[i])
                    {
                        var reg = allRegisters.First((x => x.Regnum == f.UpvalueRegisterBinding[i]));
                        f.UpvalueBindings.Add(reg);
                    }
                    else
                    {
                        f.UpvalueBindings.Add(UpvalueBindings[f.UpvalueRegisterBinding[i]]);
                    }
                }
            }

            /*foreach (var b in BlockList)
            {
                for (int i = 0; i < b.Instructions.Count(); i++)
                {
                    // Recognize a closure instruction
                    if (b.Instructions[i] is Assignment a && a.Right is Closure c)
                    {
                        for (int j = 0; j < c.Function.UpvalCount; j++)
                        {
                            if (c.Function.UpvalueIsStackBinding[i])
                            {
                                
                            }
                            else
                            {
                                // Otherwise inherit the upvalue
                                c.Function.UpvalueBindings.Add(UpvalueBindings[f.UpvalueRegisterBinding[i]]);
                            }
                        }
                    }
                }
            }*/
        }

        private HashSet<Identifier> definitelyLocal = new HashSet<Identifier>();

        // Given the IR is in SSA form, this does expression propagation/substitution.
        public void PerformExpressionPropagation(bool firstpass)
        {
            // Lua function calls (and expressions in general have their bytecode generated recursively. This means for example when doing a function call,
            // the name of the function is loaded to a register first, then all the subexpressions are computed, and finally the function is called. We can
            // exploit this knowledge to determine which expressions were actually inlined into the function call in the original source code.
            if (firstpass)
            {
                foreach (var b in BlockList)
                {
                    // The pre-propagation instruction index at which each identifier was last assigned in this block.
                    var defines = new Dictionary<Identifier, int>();
                    // The set of identifiers assigned by self ops in this block.
                    var selfs = new HashSet<Identifier>();
                    for (int i = 0; i < b.Instructions.Count(); i++)
                    {
                        b.Instructions[i].PrePropagationIndex = i;
                        var defs = b.Instructions[i].GetDefines(true);
                        if (defs.Count == 1)
                        {
                            defines.Add(defs.First(), i);
                            if (b.Instructions[i] is Assignment a2 && a2.IsSelfAssignment)
                            {
                                selfs.Add(defs.First());
                            }
                        }
                        if (b.Instructions[i] is Assignment a && a.Right is FunctionCall fc && fc.Function is IdentifierReference fir)
                        {
                            fc.FunctionDefIndex = defines[fir.Identifier];
                            if (selfs.Contains(fir.Identifier))
                            {
                                // If a self op was used, the first arg will be loaded before the function name
                                fc.FunctionDefIndex--;
                            }
                        }
                        // Detect tail calls
                        else if (b.Instructions[i] is Return r && r.ReturnExpressions.Count == 1 && r.ReturnExpressions[0] is FunctionCall fc2 && fc2.Function is IdentifierReference fir2)
                        {
                            fc2.FunctionDefIndex = defines[fir2.Identifier];
                            if (selfs.Contains(fir2.Identifier))
                            {
                                // If a self op was used, the first arg will be loaded before the function name
                                fc2.FunctionDefIndex--;
                            }
                        }
                    }
                }
            }

            // Lua, with its simple one-pass compiler, doesn't really have a register allocation algorithm of any kind. Local variables are allocated to
            // a single register for the lifetime of the entire scope, while temp locals generated by the compiler for complex expressions only have a
            // lifetime for that expression (i.e. once the expression is done evaluating, that register is free to be used in that scope again. Of course,
            // we can exploit this to figure out local variables in the original source code even if they only had one use: If the next register defined
            // within the scope (dominance hierarchy) after the first use of a recently defined register is not that register, then it's likely an actual
            // local variable.
            int LocalIdentifyVisit(CFG.BasicBlock b, HashSet<Identifier> localRegs)
            {
                HashSet<Identifier> thisLocalRegs = new HashSet<Identifier>();
                thisLocalRegs.UnionWith(localRegs);

                // Set of defines that are never used in this block (very very likely to be locals)
                HashSet<Identifier> unusedDefines = new HashSet<Identifier>();

                // Set of recently used registers that are candidates for locals
                Dictionary<Identifier, Identifier> recentlyUsed = new Dictionary<Identifier, Identifier>();
                for (int i = 0; i < b.Instructions.Count(); i++)
                {
                    // First add registers such the set contains all the registers used up to this point
                    foreach (var use in b.Instructions[i].GetUses(true))
                    {
                        // If it's used it's no longer an unused definition
                        if (unusedDefines.Contains(use))
                        {
                            unusedDefines.Remove(use);
                        }

#if USE_LOCAL_REGS_HEURISTIC
                        if (thisLocalRegs.Contains(use.OriginalIdentifier))
                        {
                            // Already marked as a local
                            continue;
                        }
#endif

                        if (definitelyLocal.Contains(use))
                        {
                            // Add it to the local regs
                            thisLocalRegs.Add(use.OriginalIdentifier);
                            continue;
                        }

                        if (b.Instructions[i] is Assignment a && a.Left.Any(ir => ir.Identifier == use))
                        {
                            // Table assignments don't count towards the local heuristics
                            continue;
                        }

                        if (!recentlyUsed.ContainsKey(use.OriginalIdentifier))
                        {
                            recentlyUsed.Add(use.OriginalIdentifier, use);
                        }
                        else
                        {
                            // Double use. Definitely a local
                            definitelyLocal.Add(use);
                            thisLocalRegs.Add(use.OriginalIdentifier);
                            recentlyUsed.Remove(use.OriginalIdentifier);
                        }
                    }

                    var defines = b.Instructions[i].GetDefines(true);

                    // Self instructions have a lot of information because they always use the next available temp registers. This
                    // means that any pending uses below us that haven't been redefined yet are actually locals. Note that the SELF
                    // op actually translates to two IR ops with two registers used, so we account for that
                    if (b.Instructions[i] is Assignment self && self.IsSelfAssignment)
                    {
                        var def1 = defines.First();
                        var def2 = b.Instructions[i + 1].GetDefines(true).First(); // Second instruction

                        foreach (var k in recentlyUsed.Keys)
                        {
                            // If the reg number is less than the second define then it's a local
                            if (k.Regnum < def2.OriginalIdentifier.Regnum)
                            {
                                definitelyLocal.Add(recentlyUsed[k]);
                                thisLocalRegs.Add(k);
                            }
                        }
                        recentlyUsed.Clear();
                        i++;
                        continue;
                    }

                    foreach (var def in defines)
                    {
                        // Skip upvalue
                        if (def.IsClosureBound || def.Renamed)
                        {
                            continue;
                        }

                        // Move on if it's a known local
#if USE_LOCAL_REGS_HEURISTIC
                        if (thisLocalRegs.Contains(def.OriginalIdentifier))
                        {
                            // Make sure the def is marked as local
                            definitelyLocal.Add(def);
                            continue;
                        }
#endif

                        // Add the new def to the unused defs until it's used otherwise
                        unusedDefines.Add(def);

                        // Otherwise a quick redefine is likely a temp. Mark below as locals and above as temps
                        if (recentlyUsed.ContainsKey(def.OriginalIdentifier))
                        {
                            foreach (var k in recentlyUsed.Keys)
                            {
                                // If the reg number is less than the second define then it's a local
                                if (k.Regnum < def.OriginalIdentifier.Regnum)
                                {
                                    definitelyLocal.Add(recentlyUsed[k]);
                                    thisLocalRegs.Add(k);
                                }
                            }
                            recentlyUsed.Clear();
                            continue;
                        }
                    }
                }

                // Any unused defines at this point are locals
                foreach (var unused in unusedDefines)
                {
                    definitelyLocal.Add(unused);
                    thisLocalRegs.Add(unused.OriginalIdentifier);
                }

                // Visit next blocks in scope
                var childFirstDef = int.MaxValue;
                foreach (var succ in b.DominanceTreeSuccessors)
                {
                    var fd = LocalIdentifyVisit(succ, thisLocalRegs);
                    if (fd < childFirstDef && fd != -1)
                    {
                        childFirstDef = fd;
                    }
                }

#if USE_LOCAL_REG_NUM_HEURISTIC
                // Localize remaining identifiers with reg numbers below the first presumed temp define in the following blocks in scope
                if (childFirstDef != -1 && childFirstDef != int.MaxValue)
                {
                    foreach (var k in recentlyUsed.Keys)
                    {
                        // If the reg number is less than the second define then it's a local
                        if (k.Regnum < childFirstDef)
                        {
                            definitelyLocal.Add(recentlyUsed[k]);
                            thisLocalRegs.Add(k);
                        }
                    }
                }
#endif

                // Find the register of the first non-local define to return
                int firstTempDef = -1;
                foreach (var inst in b.Instructions)
                {
                    var defs = inst.GetDefines(true);
                    if (defs.Count == 1)
                    {
                        var def = defs.First();
                        if (!definitelyLocal.Contains(def) && !def.Renamed && !def.IsClosureBound)
                        {
                            firstTempDef = (int)def.OriginalIdentifier.Regnum;
                            if (inst is Assignment self && self.IsSelfAssignment)
                            {
                                // Again a SELF op generates two assignments-the second one being the lower reg number
                                firstTempDef--;
                            }
                            break;
                        }
                    }
                }
                return firstTempDef;
            }

            if (firstpass && AnalysisOpts.PreserveOriginalLocals)
            {
                var argregs = new HashSet<Identifier>();
                foreach (var i in Parameters)
                {
                    argregs.Add(i.OriginalIdentifier);
                }
                LocalIdentifyVisit(BeginBlock, new HashSet<Identifier>(argregs));
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var b in BlockList)
                {
                    // Propagate single assignments
                    for (int i = 0; i < b.Instructions.Count; i++)
                    {
                        var inst = b.Instructions[i];
                        foreach (var use in inst.GetUses(true))
                        {
                            if (use.DefiningInstruction != null &&
                                use.DefiningInstruction is Assignment a &&
                                a.Left.Count() == 1 && a.LocalAssignments == null &&
                                (((use.UseCount == 1 || a.Right is Constant) && !definitelyLocal.Contains(use)) || a.PropagateAlways) &&
                                !a.Left[0].Identifier.IsClosureBound)
                            {
                                // Don't substitute if this use's define was defined before the code gen for the function call even began
                                if (!a.PropagateAlways && inst is Assignment a3 && a3.Right is FunctionCall fc && (use.DefiningInstruction.PrePropagationIndex < fc.FunctionDefIndex))
                                    continue;

                                if (!a.PropagateAlways && inst is Return r && r.ReturnExpressions.Count == 1 && r.ReturnExpressions[0] is FunctionCall fc2 && (use.DefiningInstruction.PrePropagationIndex < fc2.FunctionDefIndex))
                                    continue;

                                if (a.Left.Count == 1)
                                {
                                    bool replaced = inst.ReplaceUses(use, a.Right);
                                    if (a.Block != null && replaced)
                                    {
                                        changed = true;
                                        a.Block.Instructions.Remove(a);
                                        SSAVariables.Remove(use);
                                        if (b == a.Block)
                                            i = -1;
                                    }
                                }
                            }
                        }
                    }
                    // Propagate multi assignments
                    for (int i = 0; i < b.Instructions.Count; i++)
                    {
                        var inst = b.Instructions[i];
                        if (!(inst is Assignment a) || a.Left.Count <= 1 || a.LocalAssignments != null)
                            continue;

                        if (i + a.Left.Count > b.Instructions.Count)
                            continue;

                        void MultiPropagate()
                        {
                            var copiedTemps = new List<Identifier>();

                            for (int j = 0; j < a.Left.Count; j++)
                            {
                                if (a.Left[j].HasIndex)
                                    return;

                                var id = a.Left[j].Identifier;
                                if (((id.UseCount != 1 || definitelyLocal.Contains(id)) && !a.PropagateAlways) || id.IsClosureBound)
                                    return;

                                // Must have a matching assignment from a temp variable
                                var target = b.Instructions[i + j + 1];
                                if (!(target is Assignment ta) || ta.Left.Count != 1)
                                    return;

                                if (!(ta.Right is IdentifierReference idref) || idref.HasIndex)
                                    return;

                                if (!a.Left.Select(idr => idr.Identifier).Contains(idref.Identifier))
                                    return;

                                if (copiedTemps.Contains(idref.Identifier))
                                    return;

                                copiedTemps.Add(idref.Identifier);
                            }

                            // Replace temps with destination vars and remove copies
                            for (int j = 0; j < a.Left.Count; j++)
                            {
                                var offset = copiedTemps.IndexOf(a.Left[j].Identifier);
                                a.Left[j] = ((Assignment)b.Instructions[i + offset + 1]).Left[0];
                            }

                            b.Instructions.RemoveRange(i + 1, a.Left.Count);
                        };

                        MultiPropagate();
                    }
                }

                // Lua might generate the following (decompiled) code when doing a this call on a global variable:
                //     REG0 = someGlobal
                //     REG0:someFunction(blah...)
                // This rewrites such statements to
                //     someGlobal:someFunction(blah...)
                foreach (var b in BlockList)
                {
                    for (int i = 0; i < b.Instructions.Count(); i++)
                    {
                        var inst = b.Instructions[i];
                        if (inst is Assignment a && a.Right is FunctionCall fc && fc.Args.Count > 0 &&
                            fc.Args[0] is IdentifierReference ir && !ir.HasIndex && ir.Identifier.UseCount == 2 &&
                            i > 0 && b.Instructions[i - 1] is Assignment a2 && a2.Left.Count == 1 &&
                            !a2.Left[0].HasIndex && a2.Left[0].Identifier == ir.Identifier &&
                            (a2.Right is IdentifierReference || a2.Right is Constant))
                        {
                            a.ReplaceUses(a2.Left[0].Identifier, a2.Right);
                            b.Instructions.RemoveAt(i - 1);
                            i--;
                            changed = true;
                        }
                    }
                }
            } while (changed);
        }

        private bool IsEmptyInitializerList(IInstruction instruction)
        {
            return instruction is Assignment a && a.Left.Count() == 1 && !a.Left[0].HasIndex &&
                   a.Right is InitializerList il && il.Exprs.Count == 0;
        }

        /// <summary>
        /// Detects initializer lists emitted as a series of table assignments and merges them into a single
        /// expression list
        /// </summary>
        private bool CollapseInitializerList(CFG.BasicBlock block, int startIndex)
        {
            var initAssign = block.Instructions[startIndex] as Assignment;
            var initList = initAssign.Right as InitializerList;
            var initIdentifier = initAssign.Left[0].Identifier;

            var usedIndices = new List<Constant>();
            Constant lastIndex = null;
            var changed = false;

            for (var i = startIndex + 1; i < block.Instructions.Count(); i++)
            {
                if (!(block.Instructions[i] is Assignment a))
                {
                    // Break on call/return etc referencing table
                    if (block.Instructions[i].GetUses(false).Contains(initIdentifier))
                        break;

                    continue;
                }

                // Break on assignment reading from table
                if (a.Right.GetUses(false).Contains(initIdentifier))
                    break;

                if (a.Left.Count() != 1 || a.Left[0].Identifier != initIdentifier)
                    continue;

                if (!(a.Left[0].TableIndices.FirstOrDefault() is Constant index))
                    continue;

                // No longer an initializer if indices are being reassigned
                if (usedIndices.Any(used => used.Equals(index)))
                    break;

                // Add to initializer list
                changed = true;
                initList.Exprs.Add(a.Right);
                block.Instructions.Remove(a);
                initAssign.MergeLocalAssignments(a);
                initIdentifier.UseCount--;

                // Adjust for removed instruction
                i--;

                if (initList.Assignments == null)
                    initList.Assignments = new List<Constant>();

                // Add an explicit index for a non-sequential, non-numeric, or non-one based index
                if (index.ConstType != Constant.ConstantType.ConstNumber ||
                    (lastIndex == null && index.Number != 1) ||
                    (lastIndex != null && (
                        lastIndex.ConstType != Constant.ConstantType.ConstNumber ||
                        index.Number != lastIndex.Number + 1)))
                {
                    initList.Assignments.Add(index);
                }
                else
                {
                    initList.Assignments.Add(null);
                }

                usedIndices.Add(index);
                lastIndex = index;
            }

            return changed;
        }

        /// <summary>
        /// Detects list initializers as a series of statements that serially add data to a newly initialized list
        /// </summary>
        public bool DetectListInitializers()
        {
            var changed = false;
            foreach (var b in BlockList)
            {
                for (int i = 0; i < b.Instructions.Count(); i++)
                {
                    if (IsEmptyInitializerList(b.Instructions[i]))
                    {
                        // Eat up any statements that follow that match the initializer list pattern
                        if (CollapseInitializerList(b, i))
                            changed = true;
                    }
                }
            }

            return changed;
        }

        public List<CFG.BasicBlock> PostorderTraversal(bool reverse)
        {
            var ret = new List<CFG.BasicBlock>();
            var visited = new HashSet<CFG.BasicBlock>();
            visited.Add(EndBlock);

            void Visit(CFG.BasicBlock b)
            {
                visited.Add(b);
                foreach (var succ in b.Successors)
                {
                    if (!visited.Contains(succ))
                    {
                        Visit(succ);
                    }
                }
                ret.Add(b);
            }

            Visit(BeginBlock);

            if (reverse)
            {
                ret.Reverse();
            }
            return ret;
        }

        private void NumberReversePostorder()
        {
            var ordering = PostorderTraversal(true);
            for (int i = 0; i < ordering.Count(); i++)
            {
                ordering[i].ReversePostorderNumber = i;
            }
        }

        /// <summary>
        /// An assignment is dead if it does not have any subsequent uses. These will be eliminated unless they are a function call.
        /// </summary>
        public void EliminateDeadAssignments(bool phiOnly)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                var usageCounts = new Dictionary<Identifier, int>();
                foreach (var arg in Parameters)
                {
                    usageCounts.Add(arg, 0);
                }

                // Used for phi function cycle detection
                var singleUses = new Dictionary<Identifier, PhiFunction>();

                // Do a reverse-postorder traversal to register all the definitions and uses
                foreach (var b in PostorderTraversal(true))
                {
                    // Defines/uses in phi functions
                    foreach (var phi in b.PhiFunctions)
                    {
                        var useduses = new HashSet<Identifier>();
                        foreach (var use in phi.Value.Right)
                        {
                            // If a phi function has multiple uses of the same identifier, only count it as one use for the purposes of this analysis
                            if (use != null && !useduses.Contains(use))
                            {
                                if (!usageCounts.ContainsKey(use))
                                {
                                    usageCounts.Add(use, 0);
                                }
                                usageCounts[use]++;
                                if (usageCounts[use] == 1)
                                {
                                    if (singleUses.ContainsKey(use))
                                    {
                                        singleUses.Remove(use);
                                    }
                                    else
                                    {
                                        singleUses.Add(use, phi.Value);
                                    }
                                }
                                else
                                {
                                    if (singleUses.ContainsKey(use))
                                    {
                                        singleUses.Remove(use);
                                    }
                                }
                                useduses.Add(use);
                            }
                        }
                        if (!usageCounts.ContainsKey(phi.Value.Left))
                        {
                            usageCounts.Add(phi.Value.Left, 0);
                        }
                    }

                    // Defines/uses for everything else
                    foreach (var inst in b.Instructions)
                    {
                        foreach (var use in inst.GetUses(true))
                        {
                            if (!usageCounts.ContainsKey(use))
                            {
                                usageCounts.Add(use, 0);
                            }
                            usageCounts[use]++;
                        }
                        foreach (var def in inst.GetDefines(true))
                        {
                            if (!usageCounts.ContainsKey(def))
                            {
                                usageCounts.Add(def, 0);
                            }
                        }
                    }
                }

                // Do an elimination pass
                foreach (var b in BlockList)
                {
                    // Eliminate unused phi functions
                    var phiToRemove = new List<Identifier>();
                    foreach (var phi in b.PhiFunctions)
                    {
                        if (usageCounts[phi.Value.Left] == 0)
                        {
                            changed = true;
                            phiToRemove.Add(phi.Value.Left);
                        }

                        // If this phi function has a single use, which is also a phi function, and that phi function has
                        // a single use, which is this phi function, then we have a useless phi function dependency cycle
                        // that can be broken and removed
                        if (singleUses.ContainsKey(phi.Value.Left) && singleUses.ContainsKey(singleUses[phi.Value.Left].Left) &&
                                singleUses[singleUses[phi.Value.Left].Left] == phi.Value)
                        {
                            changed = true;
                            phiToRemove.Add(phi.Value.Left);
                            singleUses[phi.Value.Left].RenameUses(phi.Value.Left, null);
                        }
                    }
                    foreach (var rem in phiToRemove)
                    {
                        foreach (var i in b.PhiFunctions[rem.OriginalIdentifier].Right)
                        {
                            if (i != null)
                            {
                                i.UseCount--;
                            }
                        }
                    }
                    phiToRemove.ForEach(x => b.PhiFunctions.Remove(x.OriginalIdentifier));

                    if (!phiOnly)
                    {
                        // Eliminate unused assignments
                        var toRemove = new List<IInstruction>();
                        foreach (var inst in b.Instructions)
                        {
                            var defs = inst.GetDefines(true);
                            if (defs.Count() == 1 && usageCounts[defs.First()] == 0)
                            {
                                changed = true;
                                if (inst is Assignment a && a.Right is FunctionCall)
                                {
                                    a.Left.Clear();
                                }
                                else
                                {
                                    toRemove.Add(inst);
                                }
                            }
                        }
                        toRemove.ForEach(x => b.Instructions.Remove(x));
                    }
                }
            }
        }

        /// <summary>
        /// Prune phi functions that don't end up being used at all by actual code
        /// </summary>
        public void PruneUnusedPhiFunctions()
        {
            var phisToKeep = new HashSet<PhiFunction>();
            var usedIdentifiers = new HashSet<Identifier>();

            // First, iterate through all the non-phi instructions to get all the used identifiers
            foreach (var b in BlockList)
            {
                foreach (var inst in b.Instructions)
                {
                    foreach (var use in inst.GetUses(true))
                    {
                        if (!usedIdentifiers.Contains(use))
                        {
                            usedIdentifiers.Add(use);
                        }
                    }
                }
            }

            // Next do an expansion cycle where phi functions that use the identifiers are marked kept, and then phi functions that the marked phi uses are also kept
            foreach (var b in BlockList)
            {
                foreach (var phi in b.PhiFunctions)
                {
                    if (!phisToKeep.Contains(phi.Value) && usedIdentifiers.Contains(phi.Value.Left))
                    {
                        phisToKeep.Add(phi.Value);
                        foreach (var use in phi.Value.Right)
                        {
                            if (!usedIdentifiers.Contains(use))
                            {
                                usedIdentifiers.Add(use);
                            }
                        }
                    }
                }
            }

            // Now prune any phi functions that aren't marked
            foreach (var b in BlockList)
            {
                var phiToRemove = new List<Identifier>();
                foreach (var phi in b.PhiFunctions)
                {
                    if (!phisToKeep.Contains(phi.Value))
                    {
                        foreach (var i in phi.Value.Right)
                        {
                            if (i != null)
                            {
                                i.UseCount--;
                            }
                        }
                        phiToRemove.Add(phi.Key);
                    }
                }
                phiToRemove.ForEach(x => b.PhiFunctions.Remove(x));
            }
        }

        private void AddLoopLatch(CFG.AbstractGraph graph, CFG.AbstractGraph.Node head, CFG.AbstractGraph.Node latch, HashSet<CFG.AbstractGraph.Node> interval)
        {
            if (!graph.LoopLatches.ContainsKey(latch))
            {
                graph.LoopLatches.Add(latch, new List<CFG.AbstractGraph.Node>());
            }
            graph.LoopLatches[latch].Add(head);

            var loopNodes = new HashSet<CFG.AbstractGraph.Node>();

            // Analyze the loop to determine the beginning of the range of postordered nodes that represent the loop
            int beginNum = head.ReversePostorderNumber;
            /*foreach (var succ in head.Successors)
            {
                if (succ.ReversePostorderNumber > beginNum && succ.ReversePostorderNumber <= latch.ReversePostorderNumber)
                {
                    beginNum = succ.ReversePostorderNumber;
                }
            }
            if (beginNum == -1)
            {
                throw new Exception("Bad loop analysis");
            }*/

            //loopNodes.Add(head);
            //head.InLoop = true;
            head.IsHead = true;
            graph.LoopHeads[head] = head;
            foreach (var l in interval.Where(x => x.ReversePostorderNumber >= beginNum && x.ReversePostorderNumber <= latch.ReversePostorderNumber))
            {
                loopNodes.Add(l);
                l.InLoop = true;
            }

            if (!graph.LoopFollows.ContainsKey(head))
            {
                CFG.LoopType type;
                if (head.Successors.Any(next => !loopNodes.Contains(next)))
                {
                    type = CFG.LoopType.LoopPretested;
                }
                else
                {
                    type = latch.Successors.Any(next => !loopNodes.Contains(next)) ? CFG.LoopType.LoopPosttested : CFG.LoopType.LoopEndless;
                }
                graph.LoopTypes[head] = type;
                List<CFG.AbstractGraph.Node> follows;
                if (type == CFG.LoopType.LoopPretested)
                {
                    follows = head.Successors.Where(next => !loopNodes.Contains(next)).ToList();
                }
                else if (type == CFG.LoopType.LoopPosttested)
                {
                    follows = latch.Successors.Where(next => !loopNodes.Contains(next)).ToList();
                }
                else
                {
                    //follows = loopNodes.SelectMany(loopNode => loopNode.Successors.Where(next => !loopNodes.Contains(next))).ToList();
                    // Heuristic: make the follow any loop successor node with a post-order number larger than the latch
                    follows = loopNodes.SelectMany(loopNode => loopNode.Successors.Where(next => next.ReversePostorderNumber > latch.ReversePostorderNumber)).ToList();
                }
                CFG.AbstractGraph.Node follow;
                if (follows.Count == 0)
                {
                    if (type != CFG.LoopType.LoopEndless)
                    {
                        throw new Exception("No follow for loop found");
                    }
                    follow = null;
                }
                else
                {
                    follow = follows.OrderBy(cand => cand.ReversePostorderNumber).First(); 
                }
                graph.LoopFollows[head] = follow;
            }
        }

        private void DetectLoopsForIntervalLevel(CFG.AbstractGraph graph)
        {
            foreach (var interval in graph.Intervals)
            {
                var head = interval.Key;
                var intervalNodes = interval.Value;
                var latches = head.Predecessors.Where(p => intervalNodes.Contains(p)).OrderBy(p => p.ReversePostorderNumber).ToList();
                foreach (var latch in latches)
                {
                    AddLoopLatch(graph, head, latch, intervalNodes);
                }
            }
            var subgraph = graph.GetIntervalSubgraph();
            if (subgraph != null)
            {
                DetectLoopsForIntervalLevel(subgraph);
                foreach (var entry in subgraph.LoopLatches)
                {
                    var parentInterval = entry.Key.IntervalGraphParent.Interval;
                    foreach (var head in entry.Value)
                    {
                        var latches = head.IntervalGraphParent.Predecessors.Where(p => parentInterval.Contains(p)).ToList();
                        var headersInLoop = subgraph.LoopHeads.Where(e => e.Value == head).Select(e => e.Key.IntervalGraphParent).ToList();
                        var intervalsInLoop = new HashSet<CFG.AbstractGraph.Node>(graph.Intervals.Where(e => (headersInLoop.Contains(e.Key)/* || e.Value == parentInterval*/)).SelectMany(e => e.Value));
                        foreach (var latch in latches)
                        {
                            AddLoopLatch(graph, head.IntervalGraphParent, latch, intervalsInLoop);
                        }
                    }
                }
            }
        }

        public void DetectLoops()
        {
            // Build an abstract graph to analyze with
            var blockIDMap = new Dictionary<CFG.BasicBlock, int>();
            var abstractNodes = new List<CFG.AbstractGraph.Node>();
            for (int i = 0; i < BlockList.Count(); i++)
            {
                blockIDMap.Add(BlockList[i], i);
                var node = new CFG.AbstractGraph.Node();
                node.OriginalBlock = BlockList[i];
                if (i == BlockList.Count() - 1)
                {
                    node.IsTerminal = true;
                }
                abstractNodes.Add(node);
            }
            foreach (var b in blockIDMap)
            {
                foreach (var pred in b.Key.Predecessors)
                {
                    abstractNodes[b.Value].Predecessors.Add(abstractNodes[blockIDMap[pred]]);
                }
                foreach (var succ in b.Key.Successors)
                {
                    abstractNodes[b.Value].Successors.Add(abstractNodes[blockIDMap[succ]]);
                }
            }

            // Calculate intervals and the graph sequence in preperation for loop detection
            var headGraph = new CFG.AbstractGraph();
            headGraph.BeginNode = abstractNodes[blockIDMap[BeginBlock]];
            headGraph.Nodes = abstractNodes;
            headGraph.CalculateIntervals();
            headGraph.LabelReversePostorderNumbers();

            DetectLoopsForIntervalLevel(headGraph);

            foreach (var latch in headGraph.LoopLatches)
            {
                foreach (var head in latch.Value)
                {
                    var b = head.OriginalBlock;
                    b.IsLoopHead = true;
                    b.LoopLatches.Add(latch.Key.OriginalBlock);
                    b.LoopType = headGraph.LoopTypes[head];
                    if(headGraph.LoopFollows[head] == null)
                        continue;
                    b.LoopFollow = headGraph.LoopFollows[head].OriginalBlock;
                    latch.Key.OriginalBlock.IsLoopLatch = true;
                }
            }
        }

        public void DetectTwoWayConditionals()
        {
            var debugVisited = new HashSet<CFG.BasicBlock>();
            HashSet<CFG.BasicBlock> Visit(CFG.BasicBlock b)
            {
                var unresolved = new HashSet<CFG.BasicBlock>();
                foreach (var succ in b.DominanceTreeSuccessors)
                {
                    if (debugVisited.Contains(succ))
                    {
                        throw new Exception("Revisited dom tree node " + succ);
                    }
                    debugVisited.Add(succ);
                    unresolved.UnionWith(Visit(succ));
                }

                if (b.Successors.Count() == 2 && b.Instructions.Last() is Jump jmp && (!b.IsLoopHead || b.LoopType != CFG.LoopType.LoopPretested))
                {
                    int maxEdges = 0;
                    CFG.BasicBlock maxNode = null;
                    foreach (var d in b.DominanceTreeSuccessors)
                    {
                        int successorsReq = 2;
                        // If there is a break or while, the follow node is only going to have one backedge
                        if (b.LoopBreakFollow != null || b.LoopContinueFollow != null)
                        {
                            successorsReq = 1;
                        }
                        if (d.Predecessors.Count() >= successorsReq && d.Predecessors.Count() > maxEdges && !d.IsContinueNode && !d.IsBreakNode && d != EndBlock)
                        {
                            maxEdges = d.Predecessors.Count();
                            maxNode = d;
                        }
                    }
                    // Heuristic: if the true branch leads to a return or is if-orphaned and the follow isn't defined already, then the follow is always the false branch
                    // If the true branch also has a follow chain defined that leads to a return or if-orphaned node, then it is also disjoint from the rest of the CFG
                    // and the false branch is the follow
                    bool isDisjoint = false;
                    var testfollow = b.Successors[0].Follow;
                    while (testfollow != null)
                    {
                        if (testfollow.Instructions.Last() is Return || testfollow.IfOrphaned)
                        {
                            isDisjoint = true;
                            break;
                        }
                        testfollow = testfollow.Follow;
                    }
                    if (maxNode == null && (b.Successors[0].Instructions.Last() is Return || b.Successors[0].IfOrphaned || isDisjoint))
                    {
                        // If the false branch leads to an isolated return node or an if-orphaned node, then we are if-orphaned, which essentially means we don't
                        // have a follow defined in the CFG. This means that to structure this, the if-orphaned node must be adopted by the next node with a CFG
                        // determined follow and this node will inherit that follow
                        if ((b.Successors[1].Instructions.Last() is Return && b.Successors[1].Predecessors.Count() == 1) || b.Successors[1].IfOrphaned)
                        {
                            b.IfOrphaned = true;
                        }
                        else
                        {
                            maxNode = b.Successors[1];
                        }
                    }
                    // If you don't match anything, but you dominate the end node, then it's probably the follow
                    if (maxNode == null && b.DominanceTreeSuccessors.Contains(EndBlock))
                    {
                        maxNode = EndBlock;
                    }

                    // If we are a latch and the false node leads to a loop head, then the follow is the loop head
                    if (maxNode == null && b.IsLoopLatch && b.Successors[1].IsLoopHead)
                    {
                        maxNode = b.Successors[1];
                    }

                    if (maxNode != null)
                    {
                        b.Follow = maxNode;
                        var unresolvedClone = new HashSet<CFG.BasicBlock>(unresolved);
                        foreach (var x in unresolvedClone)
                        {
                            if (x != maxNode && !x.Dominance.Contains(maxNode))
                            {
                                bool inc = (x.DominanceTreeSuccessors.Count() == 0);
                                // Do a BFS down the dominance heirarchy to search for a follow
                                var bfsQueue = new Queue<CFG.BasicBlock>(x.DominanceTreeSuccessors);
                                //foreach (var domsucc in x.DominanceTreeSuccessors)
                                //{
                                while (bfsQueue.Count() > 0)
                                {
                                    var domsucc = bfsQueue.Dequeue();
                                    if (domsucc.Successors.Contains(maxNode) || domsucc.Follow == maxNode)
                                    {
                                        inc = true;
                                        break;
                                    }
                                    domsucc.DominanceTreeSuccessors.ForEach(s => bfsQueue.Enqueue(s));
                                }
                                //}
                                if (x.IfOrphaned)
                                {
                                    inc = true;
                                }
                                if (inc)
                                {
                                    x.Follow = maxNode;
                                    unresolved.Remove(x);
                                }
                            }
                        }

                    }
                    else
                    {
                        unresolved.Add(b);
                    }
                }

                // The loop head or latch is the implicit follow of any unmatched conditionals
                if (b.IsLoopHead)
                {
                    foreach (var ur in unresolved)
                    {
                        // If there's a single loop latch and it has multiple predecessors, it's probably the follow
                        if (b.LoopLatches.Count == 1 && b.LoopLatches[0].Predecessors.Count() > 1)
                        {
                            ur.Follow = b.LoopLatches[0];
                        }
                        // Otherwise the detected latch (of multiple) is probably within an if statement and the head is the true follow
                        else
                        {
                            ur.Follow = b;
                        }
                    }
                    unresolved.Clear();
                }

                return unresolved;
            }

            // Unsure about this logic, but the idea is that an if chain at the end that only returns will be left unmatched and unadopted,
            // and thus the follows need to be the end blocks
            var unmatched = Visit(BeginBlock);
            foreach (var u in unmatched)
            {
                u.Follow = EndBlock;
            }
        }

        /// <summary>
        /// If conditional structuring won't detect if statements that lead to a break or continue. This pass aims to identify and structure those
        /// </summary>
        public void DetectLoopConditionals()
        {
            var visited = new HashSet<CFG.BasicBlock>();
            void Visit(CFG.BasicBlock b, CFG.BasicBlock loopHead)
            {
                visited.Add(b);
                var lhead = loopHead;
                if (b.IsLoopHead)
                {
                    lhead = b;
                }
                foreach (var succ in b.Successors)
                {
                    if (!visited.Contains(succ))
                    {
                        Visit(succ, lhead);
                    }
                }

                // Detect unstructured if statements
                if (lhead != null && b.Successors.Count() == 2 && b.Instructions.Last() is Jump jmp && !(b.IsLoopHead && b.LoopType == CFG.LoopType.LoopPretested))
                {
                    // An if statement is unstructured but recoverable if it has a forward edge to the loop follow (break) or head (continue) on the left or right
                    foreach (var succ in b.DominanceTreeSuccessors)
                    {
                        if (succ.IsLoopLatch)
                        {
                            continue;
                        }

                        // Mark breaks
                        if (succ.Successors.Contains(lhead.LoopFollow))
                        {
                            succ.IsBreakNode = true;
                            b.LoopBreakFollow = lhead.LoopFollow;
                        }
                        // Mark continues
                        if (succ.Successors.Contains(lhead))
                        {
                            succ.IsContinueNode = true;
                            b.LoopContinueFollow = lhead.LoopContinueFollow;
                        }
                    }
                }
            }
            Visit(BeginBlock, null);
        }

        public void StructureCompoundConditionals()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                NumberReversePostorder();
                foreach (var node in PostorderTraversal(false))
                {
                    if (node.Instructions.Count > 0 && node.Instructions.Last() is Jump c && c.Conditional && c.Condition is BinOp bo && bo.Operation == BinOp.OperationType.OpLoopCompare)
                    {
                        continue;
                    }
                    if (node.Successors.Count() == 2 && node.Instructions.Last() is Jump n)
                    {
                        var t = node.Successors[0];
                        var e = node.Successors[1];
                        if (t.Successors.Count() == 2 && t.Instructions.First() is Jump tj && t.Predecessors.Count() == 1)
                        {
                            if (t.Successors[0] == e && t.Successors[1] != e)
                            {
                                //var newCond = new BinOp(new UnaryOp(n.Condition, UnaryOp.OperationType.OpNot), tj.Condition, BinOp.OperationType.OpOr);
                                Expression newCond;
                                if (n.Condition is BinOp b && b.IsCompare())
                                {
                                    newCond = new BinOp(((BinOp)n.Condition).NegateCondition(), tj.Condition, BinOp.OperationType.OpOr);
                                }
                                else
                                {
                                    newCond = new BinOp(new UnaryOp(n.Condition, UnaryOp.OperationType.OpNot), tj.Condition, BinOp.OperationType.OpOr);
                                }
                                n.Condition = newCond;
                                if (t.Follow != null)
                                {
                                    node.Follow = (node.Follow.ReversePostorderNumber > t.Follow.ReversePostorderNumber) ? node.Follow : t.Follow;
                                }
                                node.Successors[1] = t.Successors[1];
                                n.BBDest = node.Successors[1];
                                var i = t.Successors[1].Predecessors.IndexOf(t);
                                t.Successors[1].Predecessors[i] = node;
                                node.Successors[0] = e;
                                i = t.Successors[0].Predecessors.IndexOf(t);
                                //e.Predecessors[i] = node;
                                BlockList.Remove(t);
                                e.Predecessors.Remove(t);
                                t.Successors[1].Predecessors.Remove(t);
                                changed = true;
                            }
                            else if (t.Successors[1] == e)
                            {
                                var newCond = new BinOp(n.Condition, tj.Condition, BinOp.OperationType.OpAnd);
                                n.Condition = newCond;
                                if (t.Follow != null)
                                {
                                    node.Follow = (node.Follow.ReversePostorderNumber > t.Follow.ReversePostorderNumber) ? node.Follow : t.Follow;
                                }
                                node.Successors[0] = t.Successors[0];
                                var i = t.Successors[0].Predecessors.IndexOf(t);
                                t.Successors[0].Predecessors[i] = node;
                                e.Predecessors.Remove(t);
                                BlockList.Remove(t);
                                changed = true;
                            }
                        }
                        else if (e.Successors.Count() == 2 && e.Instructions.First() is Jump ej && e.Predecessors.Count() == 1)
                        {
                            if (e.Successors[0] == t)
                            {
                                var newCond = new BinOp(new UnaryOp(n.Condition, UnaryOp.OperationType.OpNot), ej.Condition, BinOp.OperationType.OpOr);
                                n.Condition = newCond;
                                if (e.Follow != null)
                                {
                                    node.Follow = (node.Follow.ReversePostorderNumber > e.Follow.ReversePostorderNumber) ? node.Follow : e.Follow;
                                }
                                node.Successors[1] = e.Successors[1];
                                n.BBDest = node.Successors[1];
                                var i = e.Successors[1].Predecessors.IndexOf(e);
                                e.Successors[1].Predecessors[i] = node;
                                t.Predecessors.Remove(e);
                                BlockList.Remove(e);
                                changed = true;
                            }
                            else if (e.Successors[1] == t)
                            {
                                // TODO: not correct
                                throw new Exception("this is used so fix it");
                                //var newCond = new BinOp(n.Condition, ej.Condition, BinOp.OperationType.OpOr);
                                //n.Condition = newCond;
                                //if (e.Follow != null)
                                //{
                                //    node.Follow = (node.Follow.ReversePostorderNumber > e.Follow.ReversePostorderNumber) ? node.Follow : e.Follow;
                                //}
                                //node.Successors[1] = e.Successors[0];
                                //var i = e.Successors[0].Predecessors.IndexOf(e);
                                //e.Successors[0].Predecessors[i] = node;
                                //t.Predecessors.Remove(e);
                                //BlockList.Remove(e);
                                //changed = true;
                            }
                        }
                    }
                }
            }
            ComputeDominance();
        }

        /// <summary>
        /// Sometimes, due to if edges always leading to returns, the follows selected aren't always the most optimal for clean lua generation,
        /// even though they technically generate correct code. For example, you might get:
        /// if a then
        ///     blah
        /// elseif b then
        ///     blah
        /// else
        ///     if c then
        ///         return
        ///     elseif d then
        ///         blah
        ///     end
        /// end
        /// 
        /// This can be simplified into a single if else chain. The problem is since "if c then" leads to a return, there's never an explicit jump
        /// to the last block, or the true logical follow. It becomes "orphaned" and is adopted by "elseif d then" as the follow. This pass detects such
        /// cases and simplifies them.
        /// </summary>
        public void SimplifyIfElseFollowChain()
        {
            bool IsIsolated(CFG.BasicBlock b, CFG.BasicBlock target)
            {
                var visited = new HashSet<CFG.BasicBlock>();
                var queue = new Queue<CFG.BasicBlock>();

                queue.Enqueue(b);
                visited.Add(b);
                while (queue.Count() > 0)
                {
                    var c = queue.Dequeue();
                    if (c == target)
                    {
                        return false;
                    }
                    foreach (var succ in c.Successors)
                    {
                        if (!visited.Contains(succ))
                        {
                            queue.Enqueue(succ);
                            visited.Add(succ);
                        }
                    }
                }

                // No follow found
                return true;
            }

            // This relies on reverse postorder
            NumberReversePostorder();

            var processed = new HashSet<CFG.BasicBlock>();
            foreach (var b in PostorderTraversal(false))
            {
                var chain = new List<CFG.BasicBlock>();
                if (b.Follow != null)
                {
                    var iter = b;
                    CFG.BasicBlock highestFollow = b.Follow;
                    int highestFollowNumber = b.Follow.ReversePostorderNumber;
                    chain.Add(b);
                    while (!processed.Contains(iter) && iter.Successors.Count() == 2 && 
                        iter.Follow == iter.Successors[1] && iter.Successors[1].Instructions.Count() == 1 && IsIsolated(iter.Successors[0], b.Follow)
                        && b.Successors[1] != iter && iter.Follow.Predecessors.Count() == 1)
                    {
                        processed.Add(iter);
                        iter = iter.Follow;
                        chain.Add(iter);
                        if (iter.Follow != null && iter.Follow.ReversePostorderNumber > highestFollowNumber)
                        {
                            highestFollowNumber = iter.Follow.ReversePostorderNumber;
                            highestFollow = iter.Follow;
                        }
                    }
                    if (highestFollow != null && chain.Last().Successors.Count() == 2)
                    {
                        foreach (var c in chain)
                        {
                            var oldf = c.Follow;
                            var newf = chain.Last().Follow;

                            // Update any matching follows inside the dominance tree of the true branch
                            var toVisit = new Stack<CFG.BasicBlock>();
                            toVisit.Push(c.Successors[0]);
                            while (toVisit.Count() > 0)
                            {
                                var v = toVisit.Pop();
                                if (v.Follow == oldf)
                                {
                                    v.Follow = newf;
                                }
                                foreach (var d in v.DominanceTreeSuccessors)
                                {
                                    toVisit.Push(d);
                                }
                            }
                            c.Follow = newf;
                        }
                    }
                }
                processed.Add(b);
            }
        }

        /// <summary>
        /// Does global liveness analysis to verify no copies are needed coming out of SSA form
        /// </summary>
        public void VerifyLivenessNoInterference()
        {
            // Just computes liveout despite the name
            ComputeGlobalLiveness(SSAVariables);

            var globalLiveness = new Dictionary<Identifier, HashSet<Identifier>>();
            // Initialise the disjoint sets
            foreach (var id in SSAVariables)
            {
                globalLiveness.Add(id, new HashSet<Identifier>() { id });
            }

            // Do a super shitty unoptimal union find algorithm to merge all the global ranges using phi functions
            // Rewrite this with a proper union-find if performance becomes an issue (lol)
            foreach (var b in BlockList)
            {
                foreach (var phi in b.PhiFunctions.Values)
                {
                    foreach (var r in phi.Right)
                    {
                        if (phi.Left != null && globalLiveness[phi.Left] != globalLiveness[r])
                        {
                            globalLiveness[phi.Left].UnionWith(globalLiveness[r]);
                            globalLiveness[r] = globalLiveness[phi.Left];
                        }
                    }
                }
            }

            foreach (var b in BlockList)
            {
                var liveNow = new HashSet<Identifier>(b.LiveOut);
                for (int i = b.Instructions.Count() - 1; i >= 0; i--)
                {
                    var defs = b.Instructions[i].GetDefines(true);
                    foreach (var def in defs)
                    {
                        foreach (var live in liveNow)
                        {
                            if (live != def && live.OriginalIdentifier == def.OriginalIdentifier)
                            {
                                Console.WriteLine($@"Warning: SSA live range interference detected in function {DebugID}. Results are probably wrong.");
                            }
                        }
                        liveNow.Remove(def);
                    }
                    foreach (var use in b.Instructions[i].GetUses(true))
                    {
                        liveNow.Add(use);
                    }
                }
            }
        }

        /// <summary>
        /// Naive method to convert out of SSA. Not guaranteed to produce correct code since no liveness/interferance analysis is done.
        /// This method is no longer used because it actually sucked
        /// </summary>
        public void DropSSANaive()
        {
            // Do a postorder traversal down the CFG and use the phi functions to create a map of renamings
            HashSet<CFG.BasicBlock> visited = new HashSet<CFG.BasicBlock>();
            HashSet<CFG.BasicBlock> processed = new HashSet<CFG.BasicBlock>();
            var remapCache = new Dictionary<CFG.BasicBlock, Dictionary<Identifier, Identifier>>();

            // This is used to propagate replacements induced by a loop latch down a dominance heirarchy 
            void BackPropagate(CFG.BasicBlock b, Dictionary<Identifier, Identifier> inReplacements)
            {
                // Rename variables in the block by traversing in reverse order
                for (int i = b.Instructions.Count() - 1; i >= 0; i--)
                {
                    var inst = b.Instructions[i];
                    var defs = inst.GetDefines(true);
                    foreach (var def in defs)
                    {
                        if (inReplacements.ContainsKey(def))
                        {
                            inst.RenameDefines(def, inReplacements[def]);
                            inReplacements.Remove(def);
                        }
                    }
                    foreach (var use in inst.GetUses(true))
                    {
                        if (inReplacements.ContainsKey(use))
                        {
                            inst.RenameUses(use, inReplacements[use]);
                        }
                    }
                }

                foreach (var succ in b.DominanceTreeSuccessors)
                {
                    BackPropagate(succ, inReplacements);
                }
            }

            var globalRenames = new Dictionary<Identifier, Identifier>();
            var globalRenamesInv = new Dictionary<Identifier, Identifier>();

            Dictionary<Identifier, Identifier> Visit(CFG.BasicBlock b)
            {
                visited.Add(b);
                // A set of mappings to rename variables induced by phi functions
                var replacements = new Dictionary<Identifier, Identifier>();
                foreach (var succ in b.Successors)
                {
                    Dictionary<Identifier, Identifier> previsited = null;
                    if (!visited.Contains(succ))
                    {
                        previsited = Visit(succ);
                    }
                    else
                    {
                        if (remapCache.ContainsKey(succ))
                        {
                            previsited = remapCache[succ];
                        }
                    }
                    if (previsited != null)
                    {
                        foreach (var rep in previsited)
                        {
                            if (!replacements.ContainsKey(rep.Key))
                            {
                                replacements.Add(rep.Key, rep.Value);
                            }
                        }
                    }
                }


                // First rename and delete phi functions by renaming the arguments to the assignment
                var phiuses = new HashSet<Identifier>();
                foreach (var phi in b.PhiFunctions)
                {
                    // If the def is renamed by a later instruction, go ahead and rename it
                    if (replacements.ContainsKey(phi.Value.Left))
                    {
                        phi.Value.Left = replacements[phi.Value.Left];
                    }
                    var def = phi.Value.Left;
                    foreach (var use in phi.Value.Right)
                    {
                        phiuses.Add(use);
                        if (replacements.ContainsKey(use))
                        {
                            if (replacements[use] != def)
                            {
                                //throw new Exception("Conflicting phi function renames live at the same time");
                                /*if (!globalRenamesInv.ContainsKey(replacements[use]))
                                {
                                    globalRenames.Add(replacements[use], def);
                                    globalRenamesInv.Add(def, replacements[use]);
                                }
                                else
                                {
                                    globalRenames[globalRenamesInv[replacements[use]]] = def;
                                    globalRenamesInv.Add(def, globalRenamesInv[replacements[use]]);
                                    globalRenamesInv.Remove(replacements[use]);
                                }
                                replacements[use] = def;*/
                            }
                        }
                        else
                        {
                            replacements.Add(use, def);
                        }
                    }
                }
                b.PhiFunctions.Clear();

                // Rename variables in the block by traversing in reverse order
                for (int i = b.Instructions.Count() - 1; i >= 0; i--)
                {
                    var inst = b.Instructions[i];
                    var defs = inst.GetDefines(true);
                    foreach (var def in defs)
                    {
                        if (replacements.ContainsKey(def))
                        {
                            inst.RenameDefines(def, replacements[def]);
                            // Only retire this replacement if it wasn't used by a phi function in this block
                            if (!phiuses.Contains(def))
                            {
                                replacements.Remove(def);
                            }
                        }
                    }
                    foreach (var use in inst.GetUses(true))
                    {
                        if (replacements.ContainsKey(use))
                        {
                            inst.RenameUses(use, replacements[use]);
                        }
                    }
                }
                processed.Add(b);

                // If we are the first block, rename the function arguments
                if (b == BeginBlock)
                {
                    for (int a = 0; a < Parameters.Count(); a++)
                    {
                        if (replacements.ContainsKey(Parameters[a]))
                        {
                            Parameters[a] = replacements[Parameters[a]];
                        }
                    }
                }

                // Propagate the replacements to children if this is a latch (i.e. induces a loop) and the head was already processed
                foreach (var succ in b.Successors)
                {
                    if (processed.Contains(succ) && succ.IsLoopHead)
                    {
                        BackPropagate(succ, replacements);
                    }
                }

                remapCache.Add(b, replacements);
                return replacements;
            }

            Visit(BeginBlock);

            // Go through all blocks/instructions and do the remaining renames
            foreach (var b in BlockList)
            {
                foreach (var i in b.Instructions)
                {
                    foreach (var use in i.GetUses(true))
                    {
                        if (globalRenames.ContainsKey(use))
                        {
                            i.RenameUses(use, globalRenames[use]);
                        }
                    }
                    foreach (var use in i.GetDefines(true))
                    {
                        if (globalRenames.ContainsKey(use))
                        {
                            i.RenameDefines(use, globalRenames[use]);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Drop SSA by simply dropping the subscripts. This requires no interfence in the live ranges of the definitions
        /// </summary>
        public void DropSSADropSubscripts()
        {
            foreach (var b in BlockList)
            {
                foreach (var phi in b.PhiFunctions)
                {
                    b.PhiMerged.Add(phi.Value.Left.OriginalIdentifier);
                }
                b.PhiFunctions.Clear();
                foreach (var i in b.Instructions)
                {
                    foreach (var def in i.GetDefines(true))
                    {
                        if (def.OriginalIdentifier != null)
                            i.RenameDefines(def, def.OriginalIdentifier);
                    }
                    foreach (var use in i.GetUses(true))
                    {
                        if (use.OriginalIdentifier != null)
                            i.RenameUses(use, use.OriginalIdentifier);
                    }
                }
            }
            for (int a = 0; a < Parameters.Count(); a++)
            {
                if (Parameters[a].OriginalIdentifier != null)
                    Parameters[a] = Parameters[a].OriginalIdentifier;
            }

            int counter = 0;
            Identifier NewName(Identifier orig)
            {
                var newName = new Identifier();
                newName.Name = orig.Name + $@"_{counter}";
                counter++;
                newName.IType = Identifier.IdentifierType.Register;
                newName.OriginalIdentifier = orig;
                return newName;
            }

            // If we have debug information, we can split up variables based on if a definition is associated with the start
            // of a local variable. If so, everything dominated by the containing block is renamed to that definition
            void visit(CFG.BasicBlock b, Dictionary<Identifier, Identifier> replacements)
            {
                var newreplacements = new Dictionary<Identifier, Identifier>(replacements);

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var phi in b.PhiMerged.ToList())
                    {
                        if (newreplacements.ContainsKey(phi))
                        {
                            b.PhiMerged.Remove(phi);
                            b.PhiMerged.Add(newreplacements[phi]);
                            changed = true;
                        }
                    }
                }
                // Walk down all the instructions, replacing things that need to be replaced and renaming as needed
                foreach (var instruction in b.Instructions)
                {
                    changed = true;
                    bool reassigned = false;
                    Identifier newdef = null;
                    while (changed)
                    {
                        changed = false;
                        foreach (var use in instruction.GetUses(true))
                        {
                            if (newreplacements.ContainsKey(use) && newreplacements[use] != newdef)
                            {
                                instruction.RenameUses(use, newreplacements[use]);
                                changed = true;
                            }
                        }
                        foreach (var def in instruction.GetDefines(true))
                        {
                            if (instruction is Assignment a && a.LocalAssignments != null && !reassigned)
                            {
                                var newname = NewName(def);
                                instruction.RenameDefines(def, newname);
                                if (newreplacements.ContainsKey(def))
                                {
                                    newreplacements[def] = newname;
                                    newdef = newname;
                                }
                                else
                                {
                                    newreplacements.Add(def, newname);
                                    newdef = newname;
                                }
                                changed = true;
                                reassigned = true;
                            }
                            else if (newreplacements.ContainsKey(def))
                            {
                                instruction.RenameDefines(def, newreplacements[def]);
                                changed = true;
                            }
                        }
                    }
                }

                // Propagate to children in the dominance heirarchy
                foreach (var succ in b.DominanceTreeSuccessors)
                {
                    visit(succ, newreplacements);
                }
            }
            visit(BeginBlock, new Dictionary<Identifier, Identifier>());
        }

        /// <summary>
        /// Detects and annotates declarations of local variables. These are the first definitions of variables
        /// in a dominance heirarchy.
        /// </summary>
        public void AnnotateLocalDeclarations()
        {
            // This is kinda both a pre and post-order traversal of the dominance heirarchy. In the pre traversal,
            // first local definitions are detected, marked, and propagated down the graph so that they aren't marked
            // again. In the postorder traversal, these marked definitions are backpropagated up the dominance heirarchy.
            // If a node gets multiple marked nodes for the same variable from its children in the dominance heirarchy,
            // a new local assignment must be inserted right before the node splits.
            Dictionary<Identifier, List<Assignment>> visit(CFG.BasicBlock b, HashSet<Identifier> declared)
            {
                var newdeclared = new HashSet<Identifier>(declared);
                var declaredAssignments = new Dictionary<Identifier, List<Assignment>>();

                // Go through the graph and mark declared nodes
                foreach (var inst in b.Instructions)
                {
                    if (inst is Assignment a && a.Left.Count() > 0)
                    {
                        foreach (var def in a.GetDefines(true))
                        {
                            // If the definition has been renamed at this point then it's from a parent closure and should not be made a local
                            if (!def.Renamed && !newdeclared.Contains(def))
                            {
                                newdeclared.Add(def);
                                a.IsLocalDeclaration = true;
                                declaredAssignments.Add(def, new List<Assignment>() { a });
                            }
                        }
                    }
                }

                // Visit and merge the children in the dominance heirarchy
                var inherited = new Dictionary<Identifier, List<Assignment>>();
                var phiinduced = new HashSet<Identifier>();
                foreach (var succ in b.DominanceTreeSuccessors)
                {
                    var cdeclared = visit(succ, newdeclared);
                    foreach (var entry in cdeclared)
                    {
                        if (!inherited.ContainsKey(entry.Key))
                        {
                            inherited.Add(entry.Key, new List<Assignment>(entry.Value));
                        }
                        else
                        {
                            inherited[entry.Key].AddRange(entry.Value);
                        }
                    }
                    phiinduced.UnionWith(succ.PhiMerged);
                }
                foreach (var entry in inherited)
                {
                    if (entry.Value.Count() > 1 && phiinduced.Contains(entry.Key))
                    {
                        // Multiple incoming declarations that all have the same use need to be merged
                        var assn = new Assignment(entry.Key, new Constant(Constant.ConstantType.ConstNil, -1));
                        assn.IsLocalDeclaration = true;
                        b.Instructions.Insert(b.Instructions.Count() - 1, assn);
                        declaredAssignments.Add(entry.Key, new List<Assignment>() { assn });
                        foreach (var e in entry.Value)
                        {
                            e.IsLocalDeclaration = false;
                        }
                    }
                    else
                    {
                        declaredAssignments.Add(entry.Key, entry.Value);
                    }
                }

                return declaredAssignments;
            }

            visit(BeginBlock, new HashSet<Identifier>(Parameters));
        }

        /// <summary>
        /// Inserts parentheses in all the expressions if they are needed (i.e. the result of an operation is used by an operation
        /// with lower precedence: a + b * c + d would become (a + b) * (c + d) for certain expression trees for example
        /// </summary>
        public void Parenthesize()
        {
            foreach (var b in BlockList)
            {
                foreach (var i in b.Instructions)
                {
                    i.Parenthesize();
                }
            }
        }

        /// <summary>
        /// Mark empty returns at the end of a function as implicit
        /// </summary>
        public void MarkImplicitReturn()
        {
            while (Instructions.FindLast(i => !(i is Return r) || !r.IsImplicit) is Return r
                && r.ReturnExpressions.Count == 0)
            {
                r.IsImplicit = true;
            }
        }

        public void ConvertToAST(bool lua51 = false)
        {
            // Traverse all the nodes in post-order and try to convert jumps to if statements
            var usedFollows = new HashSet<CFG.BasicBlock>();

            // Heads of for statements
            var forHeads = new HashSet<CFG.BasicBlock>();

            var relocalize = new HashSet<Identifier>();

            // Order the blocks sequentially
            for (int i = 0; i < BlockList.Count; i++)
            {
                BlockList[i].OrderNumber = i;
            }

            // Step 1: build the AST for ifs/loops based on follow information
            foreach (var node in PostorderTraversal(true))
            {
                // Search instructions for identifiers we need to relocalize
                foreach (var inst in node.Instructions)
                {
                    if (inst is Assignment asn)
                    {
                        foreach (var def in inst.GetDefines(true))
                        {
                            if (relocalize.Contains(def))
                            {
                                asn.IsLocalDeclaration = true;
                                relocalize.Remove(def);
                            }
                        }
                    }
                }

                // A for loop is a pretested loop where the follow does not match the head
                if (node.LoopFollow != null && node.LoopFollow != node && node.Predecessors.Count() >= 2 && node.LoopType == CFG.LoopType.LoopPretested)
                {
                    var loopInitializer = node.Predecessors.First(x => !node.LoopLatches.Contains(x));

                    // Match a numeric for
                    if (node.Instructions.Last() is Jump loopJump &&
                        loopJump.Condition is BinOp loopCondition &&
                        loopCondition.Operation == BinOp.OperationType.OpLoopCompare
                    )
                    {

                        var nfor = new NumericFor();
                        nfor.Limit = loopCondition.Right;
                        Identifier loopvar = (loopCondition.Left as IdentifierReference).Identifier;
                        var incinst = node.Instructions[node.Instructions.Count() - 2];


                        if ((incinst as Assignment).Right.ToString() == "nil")
                        {
                            Console.WriteLine($"(incinst as Assignment).Right - {(incinst as Assignment).Right}");

                            Expression temp = new Constant((ulong)1, 999);

                            (incinst as Assignment).Right = temp;

                            Console.WriteLine($"(incinst as Assignment).Right - {(incinst as Assignment).Right}");
                        }
                       
                        nfor.Increment = ((incinst as Assignment).Right as BinOp).Right;

                        // Remove the sub instruction at the end
                        // loopInitializer.Instructions.RemoveAt(loopInitializer.Instructions.Count - 2);

                        // Extract the step variable definition
                        if (loopInitializer.GetInstruction(loopInitializer.Instructions.Count - 2) is Assignment incassn)
                        {
                            nfor.Increment = incassn.Right;
                            if (incassn.IsLocalDeclaration)
                            {
                                relocalize.Add(incassn.Left[0].Identifier);
                            }
                            loopInitializer.Instructions.RemoveAt(loopInitializer.Instructions.Count - 2);
                        }

                        // Extract the limit variable definition
                        if (loopInitializer.GetInstruction(loopInitializer.Instructions.Count - 2) is Assignment limitassn)
                        {
                            nfor.Limit = limitassn.Right;
                            if (limitassn.IsLocalDeclaration)
                            {
                                relocalize.Add(limitassn.Left[0].Identifier);
                            }
                            loopInitializer.Instructions.RemoveAt(loopInitializer.Instructions.Count - 2);
                        }

                        // Extract the initializer variable definition
                        if (loopInitializer.GetInstruction(loopInitializer.Instructions.Count - 2) is Assignment initassn)
                        {
                            nfor.Initial = initassn;
                            if (initassn.IsLocalDeclaration)
                            {
                                relocalize.Add(initassn.Left[0].Identifier);
                                RenameLoopVariable(node, initassn.Left[0].Identifier);
                            }
                            loopInitializer.Instructions.RemoveAt(loopInitializer.Instructions.Count - 2);
                        }

                        nfor.Body = node.Successors[1];
                        nfor.Body.MarkCodegened(DebugID);
                        if (!usedFollows.Contains(node.LoopFollow))
                        {
                            nfor.Follow = node.LoopFollow;
                            usedFollows.Add(node.LoopFollow);
                            node.LoopFollow.MarkCodegened(DebugID);
                        }
                        if (loopInitializer.GetInstruction(loopInitializer.Instructions.Count - 1) is Jump)
                        {
                            loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] = nfor;
                        }
                        else
                        {
                            loopInitializer.Instructions.Add(nfor);
                        }
                        node.MarkCodegened(DebugID);
                        // The head might be the follow of an if statement, so do this to not codegen it
                        usedFollows.Add(node);


                        // Remove any jump instructions from the latches if they exist
                        foreach (var latch in node.LoopLatches)
                        {
                            if (latch.Instructions.Count > 0 && latch.Instructions.Last() is Jump jmp2 && !jmp2.Conditional && jmp2.BBDest == node)
                            {
                                latch.Instructions.RemoveAt(latch.Instructions.Count - 1);
                            }
                        }
                    }

                    // Match a generic for with a predecessor initializer
                    else if (node.Instructions.Count > 0 && node.Instructions.Last() is Jump loopJump2 && loopJump2.Condition is BinOp loopCondition2 &&
                        loopInitializer.Instructions.Count >= 2 && loopInitializer.Instructions[loopInitializer.Instructions.Count - 2] is Assignment la &&
                        la.Left[0] is IdentifierReference f && node.Instructions[0] is Assignment ba && ba.Right is FunctionCall fc &&
                        fc.Function is IdentifierReference fci && fci.Identifier == f.Identifier)
                    {
                        var gfor = new GenericFor();
                        // Search the predecessor block for the initial assignment which contains the right expression
                        Expression right = new Expression();
                        for (int i = loopInitializer.Instructions.Count() - 1; i >= 0; i--)
                        {
                            if (loopInitializer.Instructions[i] is Assignment a)
                            {
                                right = a.Right;
                                loopInitializer.Instructions.RemoveAt(i);
                                break;
                            }
                        }

                        // Loop head has the loop variables
                        if (node.Instructions.First() is Assignment a2)
                        {
                            gfor.Iterator = new Assignment(a2.Left, right);
                            node.Instructions.RemoveAt(0);
                        }
                        else
                        {
                            throw new Exception("Unkown for pattern");
                        }

                        // Body contains more loop bytecode that can be removed
                        var body = (node.Successors[0].ReversePostorderNumber < node.Successors[1].ReversePostorderNumber) ? node.Successors[0] : node.Successors[1];
                        if (body.Instructions[0] is Assignment a3)
                        {
                            body.Instructions.RemoveAt(0);
                        }

                        gfor.Body = body;
                        gfor.Body.MarkCodegened(DebugID);
                        if (!usedFollows.Contains(node.LoopFollow))
                        {
                            gfor.Follow = node.LoopFollow;
                            usedFollows.Add(node.LoopFollow);
                            node.LoopFollow.MarkCodegened(DebugID);
                        }
                        if (loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] is Jump)
                        {
                            loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] = gfor;
                        }
                        else
                        {
                            loopInitializer.Instructions.Add(gfor);
                        }
                        node.MarkCodegened(DebugID);
                        // The head might be the follow of an if statement, so do this to not codegen it
                        usedFollows.Add(node);
                    }

                    // Match a while
                    else if (node.Instructions.First() is Jump loopJump4 && loopJump4.Condition is Expression loopCondition4)
                    {
                        var whiles = new While();

                        // Loop head has condition
                        whiles.Condition = loopCondition4;
                        node.Instructions.RemoveAt(node.Instructions.Count - 1);

                        //whiles.Body = (node.Successors[0].ReversePostorderNumber > node.Successors[1].ReversePostorderNumber) ? node.Successors[0] : node.Successors[1];
                        whiles.Body = node.Successors[0];
                        whiles.Body.MarkCodegened(DebugID);
                        if (!usedFollows.Contains(node.LoopFollow))
                        {
                            whiles.Follow = node.LoopFollow;
                            usedFollows.Add(node.LoopFollow);
                            node.LoopFollow.MarkCodegened(DebugID);
                        }
                        // If there's a goto to this loop head, replace it with the while. Otherwise replace the last instruction of this node
                        if (loopInitializer.Successors.Count == 1)
                        {
                            if (loopInitializer.Instructions.Count() > 0 && loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] is Jump)
                            {
                                loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] = whiles;
                            }
                            else
                            {
                                loopInitializer.Instructions.Add(whiles);
                            }
                        }
                        else
                        {
                            node.Instructions.Add(whiles);
                        }

                        // Remove gotos in latch
                        foreach (var pred in node.Predecessors)
                        {
                            if (pred.IsLoopLatch && pred.Instructions.Last() is Jump lj && !lj.Conditional)
                            {
                                pred.Instructions.RemoveAt(pred.Instructions.Count - 1);
                            }
                        }

                        node.MarkCodegened(DebugID);
                        // The head might be the follow of an if statement, so do this to not codegen it
                        usedFollows.Add(node);
                    }

                    // Match a repeat while (single block)
                    else if (node.Instructions.Last() is Jump loopJump5 && loopJump5.Condition is Expression loopCondition5 && node.LoopLatches.Count == 1 && node.LoopLatches[0] == node)
                    {
                        var whiles = new While();
                        whiles.IsPostTested = true;

                        // Loop head has condition
                        whiles.Condition = loopCondition5;
                        node.Instructions.RemoveAt(node.Instructions.Count - 1);

                        //whiles.Body = (node.Successors[0].ReversePostorderNumber > node.Successors[1].ReversePostorderNumber) ? node.Successors[0] : node.Successors[1];
                        whiles.Body = node.Successors[1];
                        whiles.Body.MarkCodegened(DebugID);
                        if (!usedFollows.Contains(node.LoopFollow))
                        {
                            whiles.Follow = node.LoopFollow;
                            usedFollows.Add(node.LoopFollow);
                            node.LoopFollow.MarkCodegened(DebugID);
                        }
                        // If there's a goto to this loop head, replace it with the while. Otherwise replace the last instruction of this node
                        if (loopInitializer.Successors.Count == 1)
                        {
                            if (loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] is Jump)
                            {
                                loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] = whiles;
                            }
                            else
                            {
                                loopInitializer.Instructions.Add(whiles);
                            }
                        }
                        else
                        {
                            node.Instructions.Add(whiles);
                        }

                        // Remove gotos in latch
                        foreach (var pred in node.Predecessors)
                        {
                            if (pred.IsLoopLatch && pred.Instructions.Last() is Jump lj && !lj.Conditional)
                            {
                                pred.Instructions.RemoveAt(pred.Instructions.Count - 1);
                            }
                        }

                        node.MarkCodegened(DebugID);
                        // The head might be the follow of an if statement, so do this to not codegen it
                        usedFollows.Add(node);
                    }
                }

                // repeat...until loop
                if (node.LoopType == CFG.LoopType.LoopPosttested)
                {
                    var whiles = new While();
                    whiles.IsPostTested = true;

                    // Loop head has condition
                    if (node.LoopLatches.Count != 1 || node.LoopLatches[0].Instructions.Count == 0 || !(node.LoopLatches[0].Instructions.Last() is Jump))
                    {
                        throw new Exception("Unrecognized post-tested loop");
                    }
                    whiles.Condition = ((Jump)node.LoopLatches[0].Instructions.Last()).Condition;

                    whiles.Body = node;
                    if (node.LoopFollow != null && !usedFollows.Contains(node.LoopFollow))
                    {
                        whiles.Follow = node.LoopFollow;
                        usedFollows.Add(node.LoopFollow);
                        node.LoopFollow.MarkCodegened(DebugID);
                    }

                    if (node.Predecessors.Count == 2)
                    {
                        var loopInitializer = node.Predecessors.First(x => x != node.LoopLatches[0]);
                        if (loopInitializer.Successors.Count == 1)
                        {
                            if (loopInitializer.Instructions.Count > 0 && loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] is Jump)
                            {
                                loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] = whiles;
                            }
                            else
                            {
                                loopInitializer.Instructions.Add(whiles);
                            }
                        }
                        else
                        {
                            whiles.IsBlockInlined = true;
                            node.IsInfiniteLoop = true;
                            node.Instructions.Insert(0, whiles);
                        }
                    }
                    else
                    {
                        whiles.IsBlockInlined = true;
                        node.IsInfiniteLoop = true;
                        node.Instructions.Insert(0, whiles);
                    }

                    // Remove jumps in latch
                    foreach (var pred in node.Predecessors)
                    {
                        if (pred.IsLoopLatch && pred.Instructions.Last() is Jump lj)
                        {
                            pred.Instructions.RemoveAt(pred.Instructions.Count - 1);
                        }
                    }

                    node.MarkCodegened(DebugID);
                    // The head might be the follow of an if statement, so do this to not codegen it
                    usedFollows.Add(node);
                }

                // Infinite while loop
                if (node.LoopType == CFG.LoopType.LoopEndless)
                {
                    var whiles = new While();

                    // Loop head has condition
                    whiles.Condition = new Constant(true, -1);

                    whiles.Body = node;
                    if (node.LoopFollow != null && !usedFollows.Contains(node.LoopFollow))
                    {
                        whiles.Follow = node.LoopFollow;
                        usedFollows.Add(node.LoopFollow);
                        node.LoopFollow.MarkCodegened(DebugID);
                    }

                    if (node.Predecessors.Count == 2)
                    {
                        var loopInitializer = node.Predecessors.First(x => !node.LoopLatches.Contains(x));
                        if (loopInitializer.Successors.Count == 1)
                        {
                            if (loopInitializer.Instructions.Count() > 0 && loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] is Jump)
                            {
                                loopInitializer.Instructions[loopInitializer.Instructions.Count() - 1] = whiles;
                            }
                            else
                            {
                                loopInitializer.Instructions.Add(whiles);
                            }
                        }
                        else
                        {
                            whiles.IsBlockInlined = true;
                            node.IsInfiniteLoop = true;
                            node.Instructions.Insert(0, whiles);
                        }
                    }
                    else
                    {
                        whiles.IsBlockInlined = true;
                        node.IsInfiniteLoop = true;
                        node.Instructions.Insert(0, whiles);
                    }

                    // Remove gotos in latch
                    foreach (var pred in node.Predecessors)
                    {
                        if (pred.IsLoopLatch && pred.Instructions.Last() is Jump lj && !lj.Conditional)
                        {
                            pred.Instructions.RemoveAt(pred.Instructions.Count - 1);
                        }
                    }

                    node.MarkCodegened(DebugID);
                    // The head might be the follow of an if statement, so do this to not codegen it
                    usedFollows.Add(node);
                }

                // Pattern match for an if statement
                if (node.Follow != null && node.Instructions.Count > 0 && node.Instructions.Last() is Jump jmp)
                {
                    var ifStatement = new IfStatement();
                    ifStatement.Condition = jmp.Condition;
                    // Check for empty if block
                    if (node.Successors[0] != node.Follow)
                    {
                        ifStatement.True = node.Successors[0];
                        ifStatement.True.MarkCodegened(DebugID);
                        if (ifStatement.True.Instructions.LastOrDefault() is Jump lj && !lj.Conditional)
                        {
                            if (ifStatement.True.IsBreakNode)
                            {
                                ifStatement.True.Instructions[ifStatement.True.Instructions.Count() - 1] = new Break();
                            }
                            else if (ifStatement.True.IsContinueNode)
                            {
                                ifStatement.True.Instructions[ifStatement.True.Instructions.Count() - 1] = new Continue();
                            }
                            else if (ifStatement.True.IsLoopLatch || !ifStatement.True.Successors[0].IsLoopHead)
                            {
                                if (!ifStatement.True.IsLoopLatch && lj.BBDest == node.Follow && node.Successors[1] == node.Follow && ifStatement.True.OrderNumber + 1 == node.Follow.OrderNumber)
                                {
                                    // Generate an empty else statement if there's a jump to the follow, the follow is the next block sequentially, and it isn't fallthrough
                                    ifStatement.False = new CFG.BasicBlock();
                                }
                                ifStatement.True.Instructions.Remove(lj);
                            }
                        }
                        //if (ifStatement.True.Instructions.Last() is Jump && ifStatement.True.IsContinueNode)
                        if (node.IsContinueNode)// && node.Successors[0].IsLoopHead)
                        {
                            var bb = new CFG.BasicBlock();
                            bb.Instructions = new List<IInstruction>() { new Continue() };
                            ifStatement.True = bb;
                        }
                    }
                    if (node.Successors[1] != node.Follow && node.Successors[1] != node.Successors[0])
                    {
                        ifStatement.False = node.Successors[1];
                        ifStatement.False.MarkCodegened(DebugID);
                        if (ifStatement.False.Instructions.LastOrDefault() is Jump fj && !fj.Conditional)
                        {
                            if (ifStatement.False.IsBreakNode)
                            {
                                ifStatement.False.Instructions[ifStatement.False.Instructions.Count() - 1] = new Break();
                            }
                            else if (!ifStatement.False.Successors[0].IsLoopHead)
                            {
                                ifStatement.False.Instructions.Remove(fj);
                            }
                        }
                        if (node.IsContinueNode && node.Successors[1].IsLoopHead)
                        {
                            var bb = new CFG.BasicBlock();
                            bb.Instructions = new List<IInstruction>() { new Continue() };
                            ifStatement.False = bb;
                        }
                    }
                    if (!usedFollows.Contains(node.Follow))
                    {
                        ifStatement.Follow = node.Follow;
                        ifStatement.Follow.MarkCodegened(DebugID);
                        usedFollows.Add(node.Follow);
                    }
                    node.Instructions[node.Instructions.Count() - 1] = ifStatement;
                }
            }

            // Step 2: Remove Jmp instructions from follows if they exist
            foreach (var follow in usedFollows)
            {
                if (follow.Instructions.Count() > 0 && follow.Instructions.Last() is Jump jmp)
                {
                    follow.Instructions.Remove(jmp);
                }
            }

            // Step 3: For debug walk the CFG and print blocks that haven't been codegened
            foreach (var b in PostorderTraversal(true))
            {
                if (b != BeginBlock && !b.Codegened())
                {
                    Console.WriteLine($@"Warning: block_{b.BlockID} in function {DebugID} was not used in code generation. THIS IS LIKELY A DECOMPILER BUG!");
                }
            }

            IsAST = true;
        }

        private void RenameLoopVariable(CFG.BasicBlock node, Identifier identifier)
        {
            // Collect all identifier uses in preceding blocks
            var uses = new HashSet<Identifier>();
            var visited = new HashSet<CFG.BasicBlock>();
            var blocks = node.Predecessors.AsEnumerable();

            while (blocks.Any())
            {
                uses.UnionWith(blocks.SelectMany(b => b.Instructions).SelectMany(i => i.GetUses(true)));
                visited.UnionWith(blocks);
                blocks = blocks.SelectMany(b => b.Predecessors).Where(b => !visited.Contains(b));
            }

            // Take the first available letter starting from i
            char letter = 'i';

            while (uses.Where(i => i != identifier).Select(i => i.Name).Contains(letter.ToString()))
            {
                if (letter++ == 'z')
                    return;
            }

            identifier.Name = letter.ToString();
            identifier.Renamed = true;
        }

        // Rename variables from their temporary register based names to something more generic
        public void RenameVariables()
        {
            FindHeuristicNames();

            HashSet<Identifier> renamed = new HashSet<Identifier>();

            var allIdentifiers = Parameters.Concat(
                BlockList.SelectMany(
                    b => b.Instructions.OfType<Assignment>().SelectMany(
                        a => a.Left.OfType<IdentifierReference>()
                            .Where(ir => !ir.HasIndex && ir.Identifier.IType == Identifier.IdentifierType.Register && !ir.Identifier.Renamed)
                            .Select(ir => ir.Identifier))))
                .ToHashSet();
            var heuristicNeedsNumber = allIdentifiers
                .Where(p => p.HeuristicName != null)
                .GroupBy(p => p.HeuristicName)
                .ToDictionary(g => g.Key, g => g.Count() > 1);
            var heuristicNameCounts = heuristicNeedsNumber.ToDictionary(g => g.Key, g => 0);

            // Rename function arguments
            for (int i = 0; i < Parameters.Count(); i++)
            {
                renamed.Add(Parameters[i]);
                if (ArgumentNames != null && ArgumentNames.Count() > i)
                {
                    Parameters[i].Name = ArgumentNames[i].Name;
                }
                else if (Parameters[i].HeuristicName is string name)
                {
                    var suffix = "";
                    if (heuristicNeedsNumber[name]) {
                        suffix = heuristicNameCounts[name].ToString();
                        heuristicNameCounts[name]++;
                    }
                    Parameters[i].Name = Parameters[i].HeuristicName + suffix;
                }
                else if (Parameters[i].UseCount == 0)
                {
                    Parameters[i].Name = "_";
                }
                else
                {
                    Parameters[i].Name = $@"arg{i}";
                }
            }

            // Rename all the locals
            int localCounter = 0;
            foreach (var b in BlockList)
            {
                foreach (var i in b.Instructions)
                {
                    if (i is Assignment a)
                    {
                        int ll = 0;
                        foreach (var l in a.Left)
                        {
                            if (l is IdentifierReference ir && !ir.HasIndex && ir.Identifier.IType == Identifier.IdentifierType.Register && !renamed.Contains(ir.Identifier) && !ir.Identifier.Renamed)
                            {
                                renamed.Add(l.Identifier);
                                if (a.LocalAssignments != null && ll < a.LocalAssignments.Count())
                                {
                                    ir.Identifier.Name = a.LocalAssignments[ll].Name;
                                }
                                else if (ir.Identifier.HeuristicName is string name)
                                {
                                    var suffix = "";
                                    if (heuristicNeedsNumber[name]) {
                                        suffix = heuristicNameCounts[name].ToString();
                                        heuristicNameCounts[name]++;
                                    }
                                    ir.Identifier.Name = ir.Identifier.HeuristicName + suffix;
                                }
                                else
                                {
                                    ir.Identifier.Name = $@"f{DebugID}_local{localCounter}";
                                    localCounter++;
                                }
                                // Needed so upval uses by closures don't rename this
                                ir.Identifier.Renamed = true;
                            }
                            ll++;
                        }
                    }
                }
            }
        }

        // Look for common FromSoft patterns in how variables are assigned and used to determine if any of them should use more legible common names.
        private void FindHeuristicNames()
        {
            foreach (var b in BlockList)
            {
                foreach (var i in b.Instructions)
                {
                    if (i is Assignment a)
                    {
                        var value = a.Right;
                        if (a.Left.Count == 1)
                        {
                            var target = a.Left[0];
                            if (LocalMethodName(value) is string m)
                            {
                                switch (m)
                                {
                                    case "GetDist":
                                        MaybeSetHeuristicName(target, "distance");
                                        break;

                                    case "GetRandam_Int":
                                    case "GetRandam_Float":
                                        MaybeSetHeuristicName(target, "random");
                                        break;
                                }
                            }
                            else if (GlobalIdentifier(value) is String global && global.StartsWith("TARGET_"))
                            {
                                MaybeSetHeuristicName(value, "target");
                            }
                        }

                        if (LocalMethodName(value) is string method)
                        {
                            switch (method)
                            {
                                case "HasSpecialEffectId":
                                case "AddObserveSpecialEffectAttribute":
                                    MaybeSetHeuristicName(MethodReceiver(value), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 0), "target");
                                    MaybeSetHeuristicName(FunctionArg(value, 1), "speffect");
                                    break;

                                case "DoEzAction":
                                    MaybeSetHeuristicName(MethodReceiver(value), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 2), "action");
                                    break;

                                case "ClearSubGoal":
                                    MaybeSetHeuristicName(MethodReceiver(value), "goals");
                                    break;

                                case "Common_Battle_Activate":
                                    MaybeSetHeuristicName(FunctionArg(value, 0), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 1), "goals");
                                    MaybeSetHeuristicName(FunctionArg(value, 2), "probabilities");
                                    MaybeSetHeuristicName(FunctionArg(value, 3), "acts");
                                    MaybeSetHeuristicName(FunctionArg(value, 4), "fallback");
                                    break;

                                case "GetDist":
                                    MaybeSetHeuristicName(MethodReceiver(value), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 0), "target");
                                    break;

                                case "GetRandam_Int":
                                case "GetRandam_Float":
                                    MaybeSetHeuristicName(MethodReceiver(value), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 0), "min");
                                    MaybeSetHeuristicName(FunctionArg(value, 1), "max");
                                    break;

                                case "IsInterupt":
                                    MaybeSetHeuristicName(MethodReceiver(value), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 0), "interrupt");
                                    break;

                                case "AddSubGoal":
                                    MaybeSetHeuristicName(MethodReceiver(value), "goals");
                                    switch (GlobalIdentifier(FunctionArg(value, 0)))
                                    {
                                        case "GOAL_COMMON_ApproachTarget":
                                        case "GOAL_COMMON_LeaveTarget":
                                            MaybeSetHeuristicName(FunctionArg(value, 2), "movement_target");
                                            MaybeSetHeuristicName(FunctionArg(value, 3), "distance");
                                            MaybeSetHeuristicName(FunctionArg(value, 4), "turning_target");
                                            MaybeSetHeuristicName(FunctionArg(value, 5), "is_walking");
                                            MaybeSetHeuristicName(FunctionArg(value, 6), "action");
                                            break;

                                        case "GOAL_COMMON_ComboAttackTunableSpin":
                                        case "GOAL_COMMON_ComboTunable_SuccessAngle180":
                                            MaybeSetHeuristicName(FunctionArg(value, 2), "action");
                                            MaybeSetHeuristicName(FunctionArg(value, 3), "target");
                                            MaybeSetHeuristicName(FunctionArg(value, 4), "success_distance");
                                            MaybeSetHeuristicName(FunctionArg(value, 5), "turn_time_before");
                                            MaybeSetHeuristicName(FunctionArg(value, 6), "front_decision_angle");
                                            MaybeSetHeuristicName(FunctionArg(value, 7), "max_angle");
                                            MaybeSetHeuristicName(FunctionArg(value, 8), "min_angle");
                                            break;

                                        case "GOAL_COMMON_ComboRepeat":
                                            MaybeSetHeuristicName(FunctionArg(value, 2), "action");
                                            MaybeSetHeuristicName(FunctionArg(value, 3), "target");
                                            MaybeSetHeuristicName(FunctionArg(value, 4), "success_distance");
                                            MaybeSetHeuristicName(FunctionArg(value, 5), "max_angle");
                                            MaybeSetHeuristicName(FunctionArg(value, 6), "min_angle");
                                            MaybeSetHeuristicName(FunctionArg(value, 7), "success_angle");
                                            break;

                                        case "GOAL_COMMON_SidewayMove":
                                            MaybeSetHeuristicName(FunctionArg(value, 2), "target");
                                            MaybeSetHeuristicName(FunctionArg(value, 6), "action");
                                            break;

                                        case "GOAL_COMMON_StepSafety":
                                            MaybeSetHeuristicName(FunctionArg(value, 2), "forward_priority");
                                            MaybeSetHeuristicName(FunctionArg(value, 3), "back_priority");
                                            MaybeSetHeuristicName(FunctionArg(value, 4), "left_priority");
                                            MaybeSetHeuristicName(FunctionArg(value, 5), "right_priority");
                                            MaybeSetHeuristicName(FunctionArg(value, 6), "target");
                                            MaybeSetHeuristicName(FunctionArg(value, 7), "safe_distance");
                                            MaybeSetHeuristicName(FunctionArg(value, 8), "turn_time_before");
                                            MaybeSetHeuristicName(FunctionArg(value, 9), "success_if_impossible");
                                            break;

                                        case "GOAL_COMMON_Turn":
                                            MaybeSetHeuristicName(FunctionArg(value, 2), "target");
                                            MaybeSetHeuristicName(FunctionArg(value, 3), "font_decision_angle");
                                            MaybeSetHeuristicName(FunctionArg(value, 4), "action");
                                            MaybeSetHeuristicName(FunctionArg(value, 5), "desire");
                                            MaybeSetHeuristicName(FunctionArg(value, 6), "success_if_dead");
                                            break;

                                        // There are plenty more goals in use—these are just the ones I found in a sample AI script. Additional ones
                                        // can be added as-needed, or en masse if someone's got a lot of time on their hands.
                                    }
                                    break;
                            }
                        }
                        else if (FunctionName(value) is string function)
                        {
                            switch (function) {
                                case "Common_Battle_Activate":
                                    MaybeSetHeuristicName(FunctionArg(value, 0), "actor");
                                    MaybeSetHeuristicName(FunctionArg(value, 1), "goals");
                                    MaybeSetHeuristicName(FunctionArg(value, 2), "probabilities");
                                    MaybeSetHeuristicName(FunctionArg(value, 3), "acts");
                                    MaybeSetHeuristicName(FunctionArg(value, 4), "fallback");
                                    break;
                            }
                        }
                    }
                }
            }
        }

        // If `e` is a method call on a local variable, returns the name of the method being called.
        private string LocalMethodName(Expression e)
        {
            if (e is FunctionCall fc && fc.IsMethodCall && fc.Function is IdentifierReference ir && ir.TableIndices.Count == 1 &&
                ir.TableIndices[0] is Constant c && c.ConstType == Constant.ConstantType.ConstString &&
                ir.Identifier.IType != Identifier.IdentifierType.GlobalTable)
            {
                return c.String;
            }
            return null;
        }

        // If `e` is a function call, returns the name of the function being called.
        private string FunctionName(Expression e)
        {
            if (e is FunctionCall fc && fc.Function is IdentifierReference ir && ir.TableIndices.Count == 0 &&
                ir.Identifier.IType == Identifier.IdentifierType.Global)
            {
                return ir.Identifier.Name;
            }
            return null;
        }

        // If `e` is a reference to a global identifier, returns that identifier's name.
        private string GlobalIdentifier(Expression e)
        {
            if (e is IdentifierReference ir && !ir.HasIndex &&
                (ir.Identifier.IType == Identifier.IdentifierType.Global || ir.Identifier.IType == Identifier.IdentifierType.GlobalTable))
            {
                return ir.Identifier.Name;
            }
            return null;
        }

        // If `e` is a function call with at least `i + 1` arguments, returns the argument at the given index.
        private Expression FunctionArg(Expression e, int i)
        {
            if (e is FunctionCall fc)
            {
                // Method calls have an implicit self arg.
                if (fc.IsMethodCall) i++;
                if (i < fc.Args.Count)
                {
                    return fc.Args[i];
                }
            }
            return null;
        }

        // If `e` is a method call, returns its receiver.
        private Identifier MethodReceiver(Expression e)
        {
            if (e is FunctionCall fc) return fc.MethodReceiver;
            return null;
        }

        // If `e` is a reference to a local variable that doesn't already have a name, set its heuristic name to `name`.
        private void MaybeSetHeuristicName(Expression e, string name)
        {
            if (e is IdentifierReference ir && !ir.HasIndex)
            {
                MaybeSetHeuristicName(ir.Identifier, name);
            }
        }

        // If `e` is a reference to a local variable that doesn't already have a name, set its heuristic name to `name`.
        private void MaybeSetHeuristicName(Identifier i, string name)
        {
            if (!i.Renamed && i.HeuristicName == null &&
                (i.IType == Identifier.IdentifierType.Register || i.IType == Identifier.IdentifierType.Upvalue))
            {
                i.HeuristicName = name;
            }
        }

        public void AnnotateEnvActFunctions()
        {
            // ENV
            var EnvJapaneseMap = new Dictionary<string, string>();
            var EnvIDMap = new Dictionary<int, string>();
            var EnvNameIdentifierMap = new Dictionary<string, Identifier>();

            foreach (var env in Annotations.ESDFunctions.ESDEnvs)
            {
                EnvJapaneseMap.Add(env.JapaneseName, env.EnglishEnum);
                EnvIDMap.Add(env.ID, env.EnglishEnum);
                var id = new Identifier();
                id.Name = env.EnglishEnum;
                id.IType = Identifier.IdentifierType.Global;
                EnvNameIdentifierMap.Add(env.EnglishEnum, id);
                //Console.WriteLine(env.EnglishEnum + " = " + env.ID);
            }

            // ACT
            var ActJapaneseMap = new Dictionary<string, string>();
            var ActIDMap = new Dictionary<int, string>();
            var ActNameIdentifierMap = new Dictionary<string, Identifier>();

            foreach (var act in Annotations.ESDFunctions.ESDActs)
            {
                ActJapaneseMap.Add(act.JapaneseName, act.EnglishEnum);
                ActIDMap.Add(act.ID, act.EnglishEnum);
                var id = new Identifier();
                id.Name = act.EnglishEnum;
                id.IType = Identifier.IdentifierType.Global;
                ActNameIdentifierMap.Add(act.EnglishEnum, id);
                //Console.WriteLine(act.EnglishEnum + " = " + act.ID);
            }

            // Replace values in lua
            foreach (var b in BlockList)
            {
                foreach (var i in b.Instructions)
                {
                    foreach (var e in i.GetExpressions())
                    {
                        if (e is FunctionCall f && f.Function is IdentifierReference ir)
                        {
                            // ENV
                            if (ir.Identifier.Name == "env")
                            {
                                if (f.Args.Count() > 0)
                                {
                                    if (f.Args[0] is Constant c1 && c1.ConstType == Constant.ConstantType.ConstString)
                                    {
                                        if (EnvJapaneseMap.ContainsKey(c1.String))
                                        {
                                            f.Args[0] = new IdentifierReference(EnvNameIdentifierMap[EnvJapaneseMap[c1.String]]);
                                        }
                                    }
                                    else if (f.Args[0] is Constant c2 && c2.ConstType == Constant.ConstantType.ConstNumber)
                                    {
                                        if (EnvIDMap.ContainsKey((int)c2.Number))
                                        {
                                            f.Args[0] = new IdentifierReference(EnvNameIdentifierMap[EnvIDMap[(int)c2.Number]]);
                                        }
                                    }
                                }
                            }

                            // ACT
                            if (ir.Identifier.Name == "act")
                            {
                                if (f.Args.Count() > 0)
                                {
                                    if (f.Args[0] is Constant c1 && c1.ConstType == Constant.ConstantType.ConstString)
                                    {
                                        if (ActJapaneseMap.ContainsKey(c1.String))
                                        {
                                            f.Args[0] = new IdentifierReference(ActNameIdentifierMap[ActJapaneseMap[c1.String]]);
                                        }
                                    }
                                    else if (f.Args[0] is Constant c2 && c2.ConstType == Constant.ConstantType.ConstNumber)
                                    {
                                        if (ActIDMap.ContainsKey((int)c2.Number))
                                        {
                                            f.Args[0] = new IdentifierReference(ActNameIdentifierMap[ActIDMap[(int)c2.Number]]);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public string PrettyPrint(string funname = null)
        {
            string str = "";
            if (DebugID != 0)
            {
                if (funname == null)
                {
                    //string str = $@"function {DebugID} (";
                    str += $@"function (";
                }
                else
                {
                    //str = $@"function {DebugID} {funname}(";
                    str += $@"function {funname}(";
                }
                for (int i = 0; i < Parameters.Count(); i++)
                {
                    str += Parameters[i].ToString();
                    if (i != Parameters.Count() - 1)
                    {
                        str += ", ";
                    }
                }
                if (IsVarargs)
                {
                    if (Parameters.Count() > 0)
                    {
                        str += ", ...";
                    }
                    else
                    {
                        str += "...";
                    }
                }
                str += ")\n";
                IndentLevel += 1;
            }
            if (!IsControlFlowGraph)
            {
                foreach (var inst in Instructions)
                {
                    str += $@"{inst.OpLocation:D3} ";
                    for (int i = 0; i < IndentLevel; i++)
                    {
                        if (inst is Label && i == IndentLevel - 1)
                        {
                            str += "  ";
                            continue;
                        }
                        str += "    ";
                    }
                    str += inst.ToString() + "\n";
                }
            }
            else if (IsAST)
            {
                str += BeginBlock.PrintBlock(IndentLevel);
            }
            else
            {
                // Traverse the basic blocks odereded by their ID
                foreach (var b in BlockList.OrderBy(a => a.BlockID))
                {
                    if (b == EndBlock)
                    {
                        continue;
                    }
                    for (int i = 0; i < IndentLevel; i++)
                    {
                        if (i == IndentLevel - 1)
                        {
                            str += "  ";
                            continue;
                        }
                        str += "    ";
                    }
                    str += b.ToStringWithLoop() + "\n";
                    foreach (var inst in b.PhiFunctions.Values)
                    {
                        for (int i = 0; i < IndentLevel; i++)
                        {
                            str += "    ";
                        }
                        str += inst.ToString() + "\n";
                    }
                    foreach (var inst in b.Instructions)
                    {
                        for (int i = 0; i < IndentLevel; i++)
                        {
                            str += "    ";
                        }
                        str += inst.ToString() + "\n";
                    }

                    // Insert an implicit goto for fallthrough blocks if the destination isn't actually the next block
                    var lastinst = (b.Instructions.Count > 0) ? b.Instructions.Last() : null;
                    if (lastinst != null && ((lastinst is Jump j && j.Conditional && b.Successors[0].BlockID != (b.BlockID + 1)) ||
                        (!(lastinst is Jump) && !(lastinst is Return) && b.Successors[0].BlockID != (b.BlockID + 1))))
                    {
                        for (int i = 0; i < IndentLevel; i++)
                        {
                            str += "    ";
                        }
                        str += "(goto " + b.Successors[0].ToString() + ")" + "\n";
                    }
                }
            }
            IndentLevel -= 1;
            if (DebugID != 0)
            {
                for (int i = 0; i < IndentLevel; i++)
                {
                    str += "    ";
                }
                str += "end";
            }
            return str;
        }

        public override string ToString()
        {
            return PrettyPrint();
        }
    }
}
