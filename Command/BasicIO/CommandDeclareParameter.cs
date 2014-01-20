﻿using System;
using System.Text.RegularExpressions;

namespace kOS.Command.BasicIO
{
    [Command("DECLARE PARAMETERS? *")]
    public class CommandDeclareParameter : Command
    {
        public CommandDeclareParameter(Match regexMatch, ExecutionContext context) : base(regexMatch, context) { }

        public override void Evaluate()
        {
            if (!(ParentContext is ContextRunProgram)) throw new kOSException("DECLARE PARAMETERS can only be used within a program.", this);

            foreach (String varName in RegexMatch.Groups[1].Value.Split(','))
            {
                Variable v = FindOrCreateVariable(varName);
                if (v == null) throw new kOSException("Can't create variable '" + varName + "'", this);

                var program = (ContextRunProgram)ParentContext;
                v.Value = program.PopParameter();
            }

            State = ExecutionState.DONE;
        }
    }
}