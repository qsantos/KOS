﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using kOS.Suffixed;
using kOS.Function;
using kOS.Compilation;

namespace kOS.Execution
{
    public class CPU: IUpdateObserver
    {
        private enum Status
        {
            Running = 1,
            Waiting = 2
        }

        private readonly Stack stack;
        private readonly Dictionary<string, Variable> vars;
        private Status currentStatus;
        private double currentTime;
        private double timeWaitUntil;
        private Dictionary<string, FunctionBase> functions;
        private readonly SharedObjects shared;
        private readonly List<ProgramContext> contexts;
        private ProgramContext currentContext;
        private Dictionary<string, Variable> savedPointers;
        
        // statistics
        public double TotalCompileTime = 0D;
        private double totalUpdateTime;
        private double totalTriggersTime;
        private double totalExecutionTime;

        public int InstructionPointer
        {
            get { return currentContext.InstructionPointer; }
            set { currentContext.InstructionPointer = value; }
        }

        public double SessionTime { get { return currentTime; } }


        public CPU(SharedObjects shared)
        {
            this.shared = shared;
            this.shared.Cpu = this;
            stack = new Stack();
            vars = new Dictionary<string, Variable>();
            contexts = new List<ProgramContext>();
            if (this.shared.UpdateHandler != null) this.shared.UpdateHandler.AddObserver(this);
        }

        private void LoadFunctions()
        {
            functions = new Dictionary<string, FunctionBase>();

            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                var attr = (FunctionAttribute)type.GetCustomAttributes(typeof(FunctionAttribute), true).FirstOrDefault();
                if (attr == null) continue;

                object functionObject = Activator.CreateInstance(type);
                foreach (string functionName in attr.Names)
                {
                    if (functionName != string.Empty)
                    {
                        functions.Add(functionName, (FunctionBase)functionObject);
                    }
                }
            }
        }

        public void Boot()
        {
            // break all running programs
            currentContext = null;
            contexts.Clear();
            PushInterpreterContext();
            currentStatus = Status.Running;
            currentTime = 0;
            timeWaitUntil = 0;
            // clear stack
            stack.Clear();
            // clear variables
            vars.Clear();
            // clear interpreter
            if (shared.Interpreter != null) shared.Interpreter.Reset();
            // load functions
            LoadFunctions();
            // load bindings
            if (shared.BindingMgr != null) shared.BindingMgr.LoadBindings();
            // Booting message
            if (shared.Screen != null)
            {
                shared.Screen.ClearScreen();
                string bootMessage = "kOS Operating System\n" +
                                     "KerboScript v" + Core.VersionInfo + "\n \n" +
                                     "Proceed.\n ";
                shared.Screen.Print(bootMessage);
            }
            
            if (shared.VolumeMgr == null) { UnityEngine.Debug.Log("kOS: No volume mgr"); }
            else if (shared.VolumeMgr.CurrentVolume == null) { UnityEngine.Debug.Log("kOS: No current volume"); }
            else if (shared.ScriptHandler == null) { UnityEngine.Debug.Log("kOS: No script handler"); }
            else if (shared.VolumeMgr.CurrentVolume.GetByName("boot") != null)
            {
                shared.ScriptHandler.ClearContext("program");

                var programContext = shared.Cpu.GetProgramContext();
                programContext.Silent = true;
                var options = new CompilerOptions {LoadProgramsInSameAddressSpace = true};
                List<CodePart> parts = shared.ScriptHandler.Compile("run boot.", "program", options);
                programContext.AddParts(parts);
            }
        }

        private void PushInterpreterContext()
        {
            var interpreterContext = new ProgramContext(true);
            // initialize the context with an empty program
            interpreterContext.AddParts(new List<CodePart>());
            PushContext(interpreterContext);
        }

        private void PushContext(ProgramContext context)
        {
            UnityEngine.Debug.Log("kOS: Pushing context staring with: " + context.GetCodeFragment(0).FirstOrDefault());
            SaveAndClearPointers();
            contexts.Add(context);
            currentContext = contexts.Last();

            if (contexts.Count > 1)
            {
                shared.Interpreter.SetInputLock(true);
            }
        }

        private void PopContext()
        {
            UnityEngine.Debug.Log("kOS: Popping context " + contexts.Count);
            if (contexts.Any())
            {
                // remove the last context
                var contextRemove = contexts.Last();
                contexts.Remove(contextRemove);
                contextRemove.DisableActiveFlyByWire(shared.BindingMgr);
                UnityEngine.Debug.Log("kOS: Removed Context " + contextRemove.GetCodeFragment(0).FirstOrDefault());

                if (contexts.Any())
                {
                    currentContext = contexts.Last();
                    currentContext.EnableActiveFlyByWire(shared.BindingMgr);
                    RestorePointers();
                    UnityEngine.Debug.Log("kOS: New current context " + currentContext.GetCodeFragment(0).FirstOrDefault());
                }
                else
                {
                    currentContext = null;
                }

                if (contexts.Count == 1)
                {
                    shared.Interpreter.SetInputLock(false);
                }
            }
        }

        private void PopFirstContext()
        {
            while (contexts.Count > 1)
            {
                PopContext();
            }
        }

        // only two contexts exist now, one for the interpreter and one for the programs
        public ProgramContext GetInterpreterContext()
        {
            return contexts[0];
        }

        public ProgramContext GetProgramContext()
        {
            if (contexts.Count == 1)
            {
                PushContext(new ProgramContext(false));
            }
            return currentContext;
        }

        private void SaveAndClearPointers()
        {
            savedPointers = new Dictionary<string, Variable>();
            var pointers = new List<string>(vars.Keys.Where(v => v.Contains('*')));

            foreach (var pointerName in pointers)
            {
                savedPointers.Add(pointerName, vars[pointerName]);
                vars.Remove(pointerName);
            }
            UnityEngine.Debug.Log(string.Format("kOS: Saving and removing {0} pointers", pointers.Count));
        }

        private void RestorePointers()
        {
            int restoredPointers = 0;
            int deletedPointers = 0;

            foreach (var item in savedPointers)
            {
                if (vars.ContainsKey(item.Key))
                {
                    // if the pointer exists it means it was redefined from inside a program
                    // and it's going to be invalid outside of it, so we remove it
                    vars.Remove(item.Key);
                    deletedPointers++;
                    // also remove the corresponding trigger if exists
                    if (item.Value.Value is int)
                        RemoveTrigger((int)item.Value.Value);
                }
                else
                {
                    vars.Add(item.Key, item.Value);
                    restoredPointers++;
                }
            }

            UnityEngine.Debug.Log(string.Format("kOS: Deleting {0} pointers and restoring {1} pointers", deletedPointers, restoredPointers));
        }

        public void RunProgram(List<Opcode> program)
        {
            RunProgram(program, false);
        }

        public void RunProgram(List<Opcode> program, bool silent)
        {
            if (!program.Any()) return;

            var newContext = new ProgramContext(false, program) {Silent = silent};
            PushContext(newContext);
        }

        public void BreakExecution(bool manual)
        {
            UnityEngine.Debug.Log(string.Format("kOS: Breaking Execution {0} Contexts: {1}", manual ? "Manually" : "Automaticly", contexts.Count));
            if (contexts.Count > 1)
            {
                EndWait();

                if (manual)
                {
                    PopFirstContext();
                    shared.Screen.Print("Program aborted.");
                    shared.BindingMgr.UnBindAll();
                    PrintStatistics();
                }
                else
                {
                    bool silent = currentContext.Silent;
                    PopContext();
                    if (contexts.Count == 1 && !silent)
                    {
                        shared.Screen.Print("Program ended.");
                        shared.BindingMgr.UnBindAll();
                        PrintStatistics();
                    }
                }
            }
            else
            {
                currentContext.Triggers.Clear();   // remove all the active triggers
                SkipCurrentInstructionId();
            }
        }

        public void PushStack(object item)
        {
            stack.Push(item);
        }

        public object PopStack()
        {
            return stack.Pop();
        }

        public void MoveStackPointer(int delta)
        {
            stack.MoveStackPointer(delta);
        }

        private Variable GetOrCreateVariable(string identifier)
        {
            Variable variable;

            if (vars.ContainsKey(identifier))
            {
                variable = GetVariable(identifier);
            }
            else
            {
                variable = new Variable {Name = identifier};
                AddVariable(variable, identifier);
            }
            return variable;
        }

        private Variable GetVariable(string identifier)
        {
            identifier = identifier.ToLower();
            if (vars.ContainsKey(identifier))
            {
                return vars[identifier];
            }
            throw new Exception(string.Format("Variable {0} is not defined", identifier.TrimStart('$')));
        }

        public void AddVariable(Variable variable, string identifier)
        {
            identifier = identifier.ToLower();
            
            if (!identifier.StartsWith("$"))
            {
                identifier = "$" + identifier;
            }

            if (vars.ContainsKey(identifier))
            {
                vars.Remove(identifier);
            }

            vars.Add(identifier, variable);
        }

        public bool VariableIsRemovable(Variable variable)
        {
            return !(variable is Binding.BoundVariable);
        }

        public void RemoveVariable(string identifier)
        {
            identifier = identifier.ToLower();
            
            if (vars.ContainsKey(identifier) &&
                VariableIsRemovable(vars[identifier]))
            {
                // Tell Variable to orphan its old value now.  Faster than relying 
                // on waiting several seconds for GC to eventually call ~Variable()
                vars[identifier].Value = null;
                
                vars.Remove(identifier);
            }
        }

        public void RemoveAllVariables()
        {
            var removals = new List<string>();
            
            foreach (var kvp in vars)
            {
                if (VariableIsRemovable(kvp.Value))
                {
                    removals.Add(kvp.Key);
                }
            }

            foreach (string identifier in removals)
            {
                // Tell Variable to orphan its old value now.  Faster than relying 
                // on waiting several seconds for GC to eventually call ~Variable()
                vars[identifier].Value = null;

                vars.Remove(identifier);
            }
        }

        public object GetValue(object testValue)
        {
            // $cos     cos named variable
            // cos()    cos trigonometric function
            // cos      string literal "cos"

            if (testValue is string &&
                ((string)testValue).StartsWith("$"))
            {
                // value is a variable
                var identifier = (string)testValue;
                Variable variable = GetVariable(identifier);
                return variable.Value;
            }
            return testValue;
        }

        public void SetValue(string identifier, object value)
        {
            Variable variable = GetOrCreateVariable(identifier);
            variable.Value = value;
        }

        public object PopValue()
        {
            return GetValue(PopStack());
        }

        public void AddTrigger(int triggerFunctionPointer)
        {
            if (!currentContext.Triggers.Contains(triggerFunctionPointer))
            {
                currentContext.Triggers.Add(triggerFunctionPointer);
            }
        }

        public void RemoveTrigger(int triggerFunctionPointer)
        {
            if (currentContext.Triggers.Contains(triggerFunctionPointer))
            {
                currentContext.Triggers.Remove(triggerFunctionPointer);
            }
        }

        public void StartWait(double waitTime)
        {
            if (waitTime > 0)
            {
                timeWaitUntil = currentTime + waitTime;
            }
            currentStatus = Status.Waiting;
        }

        public void EndWait()
        {
            timeWaitUntil = 0;
            currentStatus = Status.Running;
        }

        public void Update(double deltaTime)
        {
            bool showStatistics = Config.Instance.ShowStatistics;
            Stopwatch updateWatch = null;
            Stopwatch triggerWatch = null;
            Stopwatch executionWatch = null;

            if (showStatistics) updateWatch = Stopwatch.StartNew();

            currentTime = shared.UpdateHandler.CurrentTime;

            try
            {
                PreUpdateBindings();

                if (currentContext != null && currentContext.Program != null)
                {
                    if (showStatistics) triggerWatch = Stopwatch.StartNew();
                    ProcessTriggers();
                    if (showStatistics)
                    {
                        triggerWatch.Stop();
                        totalTriggersTime += triggerWatch.ElapsedMilliseconds;
                    }

                    ProcessWait();

                    if (currentStatus == Status.Running)
                    {
                        if (showStatistics) executionWatch = Stopwatch.StartNew();
                        ContinueExecution();
                        if (showStatistics)
                        {
                            executionWatch.Stop();
                            totalExecutionTime += executionWatch.ElapsedMilliseconds;
                        }
                    }
                }

                PostUpdateBindings();
            }
            catch (Exception e)
            {
                if (shared.Logger != null)
                {
                    shared.Logger.Log(e);
                    UnityEngine.Debug.Log(stack.Dump(15));
                }

                if (contexts.Count == 1)
                {
                    // interpreter context
                    SkipCurrentInstructionId();
                }
                else
                {
                    // break execution of all programs and pop interpreter context
                    PopFirstContext();
                }
            }

            if (showStatistics)
            {
                updateWatch.Stop();
                totalUpdateTime += updateWatch.ElapsedMilliseconds;
            }
        }

        private void PreUpdateBindings()
        {
            if (shared.BindingMgr != null)
            {
                shared.BindingMgr.PreUpdate();
            }
        }

        private void PostUpdateBindings()
        {
            if (shared.BindingMgr != null)
            {
                shared.BindingMgr.PostUpdate();
            }
        }

        private void ProcessWait()
        {
            if (currentStatus == Status.Waiting && timeWaitUntil > 0)
            {
                if (currentTime >= timeWaitUntil)
                {
                    EndWait();
                }
            }
        }

        private void ProcessTriggers()
        {
            if (currentContext.Triggers.Count > 0)
            {
                int currentInstructionPointer = currentContext.InstructionPointer;
                var triggerList = new List<int>(currentContext.Triggers);

                foreach (int triggerPointer in triggerList)
                {
                    try
                    {
                        currentContext.InstructionPointer = triggerPointer;

                        bool executeNext = true;
                        while (executeNext)
                        {
                            executeNext = ExecuteInstruction(currentContext);
                        }
                    }
                    catch (Exception e)
                    {
                        RemoveTrigger(triggerPointer);
                        shared.Logger.Log(e);
                    }
                }

                currentContext.InstructionPointer = currentInstructionPointer;
            }
        }

        private void ContinueExecution()
        {
            int instructionCounter = 0;
            bool executeNext = true;
            int instructionsPerUpdate = Config.Instance.InstructionsPerUpdate;
            
            while (currentStatus == Status.Running && 
                   instructionCounter < instructionsPerUpdate &&
                   executeNext &&
                   currentContext != null)
            {
                executeNext = ExecuteInstruction(currentContext);
                instructionCounter++;
            }
        }

        private bool ExecuteInstruction(ProgramContext context)
        {
            Opcode opcode = context.Program[context.InstructionPointer];
            if (!(opcode is OpcodeEOF || opcode is OpcodeEOP))
            {
                opcode.Execute(this);
                context.InstructionPointer += opcode.DeltaInstructionPointer;
                return true;
            }
            if (opcode is OpcodeEOP)
            {
                BreakExecution(false);
                UnityEngine.Debug.LogWarning("kOS: Execution Broken");
            }
            return false;
        }

        private void SkipCurrentInstructionId()
        {
            if (currentContext.InstructionPointer < (currentContext.Program.Count - 1))
            {
                int currentInstructionId = currentContext.Program[currentContext.InstructionPointer].InstructionId;

                while (currentContext.InstructionPointer < currentContext.Program.Count &&
                       currentContext.Program[currentContext.InstructionPointer].InstructionId == currentInstructionId)
                {
                    currentContext.InstructionPointer++;
                }
            }
        }

        public void CallBuiltinFunction(string functionName)
        {
            if (functions.ContainsKey(functionName))
            {
                FunctionBase function = functions[functionName];
                function.Execute(shared);
            }
            else
            {
                throw new Exception("Call to non-existent function " + functionName);
            }
        }

        public void ToggleFlyByWire(string paramName, bool enabled)
        {
            if (shared.BindingMgr != null)
            {
                shared.BindingMgr.ToggleFlyByWire(paramName, enabled);
                currentContext.ToggleFlyByWire(paramName, enabled);
            }
        }

        public List<string> GetCodeFragment(int contextLines)
        {
            return currentContext.GetCodeFragment(contextLines);
        }

        public void PrintStatistics()
        {
            if (!Config.Instance.ShowStatistics) return;

            shared.Screen.Print(string.Format("Total compile time: {0:F3}ms", TotalCompileTime));
            shared.Screen.Print(string.Format("Total update time: {0:F3}ms", totalUpdateTime));
            shared.Screen.Print(string.Format("Total triggers time: {0:F3}ms", totalTriggersTime));
            shared.Screen.Print(string.Format("Total execution time: {0:F3}ms", totalExecutionTime));
            shared.Screen.Print(" ");

            TotalCompileTime = 0D;
            totalUpdateTime = 0D;
            totalTriggersTime = 0D;
            totalExecutionTime = 0D;
        }

        public void OnSave(ConfigNode node)
        {
            try
            {
                var contextNode = new ConfigNode("context");

                // Save variables
                if (vars.Count > 0)
                {
                    var varNode = new ConfigNode("variables");

                    foreach (var kvp in vars)
                    {
                        if (!(kvp.Value is Binding.BoundVariable) &&
                            (kvp.Value.Name.IndexOfAny(new[] { '*', '-' }) == -1))  // variables that have this characters are internal and shouldn't be persisted
                        {
                            varNode.AddValue(kvp.Key.TrimStart('$'), Persistence.ProgramFile.EncodeLine(kvp.Value.Value.ToString()));
                        }
                    }

                    contextNode.AddNode(varNode);
                }

                node.AddNode(contextNode);
            }
            catch (Exception e)
            {
                if (shared.Logger != null) shared.Logger.Log(e);
            }
        }

        public void OnLoad(ConfigNode node)
        {
            try
            {
                var scriptBuilder = new StringBuilder();

                foreach (ConfigNode contextNode in node.GetNodes("context"))
                {
                    foreach (ConfigNode varNode in contextNode.GetNodes("variables"))
                    {
                        foreach (ConfigNode.Value value in varNode.values)
                        {
                            string varValue = Persistence.ProgramFile.DecodeLine(value.value);
                            scriptBuilder.AppendLine(string.Format("set {0} to {1}.", value.name, varValue));
                        }
                    }
                }

                if (shared.ScriptHandler != null && scriptBuilder.Length > 0)
                {
                    var programBuilder = new ProgramBuilder();
                    programBuilder.AddRange(shared.ScriptHandler.Compile(scriptBuilder.ToString()));
                    List<Opcode> program = programBuilder.BuildProgram();
                    RunProgram(program, true);
                }
            }
            catch (Exception e)
            {
                if (shared.Logger != null) shared.Logger.Log(e);
            }
        }
    }
}
