﻿/*
    This file is part of HomeGenie Project source code.

    HomeGenie is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    HomeGenie is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with HomeGenie.  If not, see <http://www.gnu.org/licenses/>.
*/

/*
 *     Author: Generoso Martello <gene@homegenie.it>
 *     Project Homepage: http://homegenie.it
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

using HomeGenie.Automation.Scripting;
using HomeGenie.Service;
using HomeGenie.Service.Constants;

namespace HomeGenie.Automation.Engines
{
    public class CSharpEngine : ProgramEngineBase, IProgramEngine
    {
        // c# program fields
        private AppDomain _programDomain;
        private Type _assemblyType;
        private object _scriptInstance;
        private MethodInfo _methodRun;
        private MethodInfo _methodReset;
        private MethodInfo _methodSetup;
        private Assembly _scriptAssembly;

        private static bool _isShadowCopySet;

        public CSharpEngine(ProgramBlock pb) : base(pb)
        {
            // TODO: SetShadowCopyPath/SetShadowCopyFiles methods are deprecated...
            // TODO: create own AppDomain for "programDomain" instead of using CurrentDomain
            // TODO: and use AppDomainSetup to set shadow copy for each app domain
            // TODO: !!! verify AppDomain compatibility with mono !!!
            if (_isShadowCopySet) return;
            _isShadowCopySet = true;
            var domain = AppDomain.CurrentDomain;
            domain.SetShadowCopyPath(Path.Combine(domain.BaseDirectory, "programs"));
            domain.SetShadowCopyFiles();
        }

        public bool Load()
        {
            var success = LoadAssembly();
            if (!success && String.IsNullOrEmpty(ProgramBlock.ScriptErrors))
            {
                ProgramBlock.ScriptErrors = "Program is not compiled.";
            }
            return success;
        }

        public void Unload()
        {
            Reset();
            ProgramBlock.ActivationTime = null;
            ProgramBlock.TriggerTime = null;
            if (_programDomain == null) return;
            // Unloading program app domain...
            try
            {
                AppDomain.Unload(_programDomain);
            }
            catch
            {
                // ignored
            }
            _programDomain = null;
        }

        public override List<ProgramError> Compile()
        {
            var errors = new List<ProgramError>();

            // check for output directory
            if (!Directory.Exists(Path.GetDirectoryName(AssemblyFile)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssemblyFile));
            }

            // dispose assembly and interrupt current task (if any)
            bool wasEnabled = ProgramBlock.IsEnabled;
            ProgramBlock.ScriptErrors = "";
            ProgramBlock.IsEnabled = false;

            // clean up old assembly files
            try
            {
                // If the file to be deleted does not exist, no exception is thrown.
                File.Delete(AssemblyFile);
                File.Delete(AssemblyFile + ".mdb");
                File.Delete(AssemblyFile.Replace(".dll", ".mdb"));
                File.Delete(AssemblyFile + ".pdb");
                File.Delete(AssemblyFile.Replace(".dll", ".pdb"));
            }
            catch (Exception ex)
            {
                HomeGenieService.LogError(ex);
            }


            // DO NOT CHANGE THE FOLLOWING LINES OF CODE
            // it is a lil' trick for mono compatibility
            // since it will be caching the assembly when using the same name
            // and use the old one instead of the new one
            var tmpfile = Path.Combine("programs", Guid.NewGuid().ToString() + ".dll");
            var result = new System.CodeDom.Compiler.CompilerResults(null);
            try
            {
                result = CSharpAppFactory.CompileScript(ProgramBlock.ScriptSetup, ProgramBlock.ScriptSource, tmpfile);
            }
            catch (Exception ex)
            {
                // report errors during post-compilation process
                result.Errors.Add(new System.CodeDom.Compiler.CompilerError(ProgramBlock.Name, 0, 0, "-1", ex.Message));
            }

            if (result.Errors.Count > 0)
            {
                var sourceLines = ProgramBlock.ScriptSource.Split('\n').Length;
                foreach (System.CodeDom.Compiler.CompilerError error in result.Errors)
                {
                    var errorRow = (error.Line - CSharpAppFactory.ProgramCodeOffset);
                    var blockType = CodeBlockEnum.CR;
                    if (errorRow >= sourceLines + CSharpAppFactory.ConditionCodeOffset)
                    {
                        errorRow -= (sourceLines + CSharpAppFactory.ConditionCodeOffset);
                        blockType = CodeBlockEnum.TC;
                    }
                    if (!error.IsWarning)
                    {
                        errors.Add(new ProgramError
                        {
                            Line = errorRow,
                            Column = error.Column,
                            ErrorMessage = error.ErrorText,
                            ErrorNumber = error.ErrorNumber,
                            CodeBlock = blockType
                        });
                    }
                    else
                    {
                        var warning = String.Format("{0},{1},{2}: {3}", blockType, errorRow, error.Column,
                            error.ErrorText);
                        HomeGenie.ProgramManager.RaiseProgramModuleEvent(ProgramBlock, Properties.CompilerWarning,
                            warning);
                    }
                }
            }

            if (errors.Count != 0)
                return errors;

            // move/copy new assembly files
            // rename temp file to production file
            _scriptAssembly = result.CompiledAssembly;
            try
            {
                //string tmpfile = new Uri(value.CodeBase).LocalPath;
                File.Move(tmpfile, this.AssemblyFile);
                if (File.Exists(tmpfile + ".mdb"))
                {
                    File.Move(tmpfile + ".mdb", this.AssemblyFile + ".mdb");
                }
                if (File.Exists(tmpfile.Replace(".dll", ".mdb")))
                {
                    File.Move(tmpfile.Replace(".dll", ".mdb"), this.AssemblyFile.Replace(".dll", ".mdb"));
                }
                if (File.Exists(tmpfile + ".pdb"))
                {
                    File.Move(tmpfile + ".pdb", this.AssemblyFile + ".pdb");
                }
                if (File.Exists(tmpfile.Replace(".dll", ".pdb")))
                {
                    File.Move(tmpfile.Replace(".dll", ".pdb"), this.AssemblyFile.Replace(".dll", ".pdb"));
                }
            }
            catch (Exception ee)
            {
                HomeGenieService.LogError(ee);
            }

            if (errors.Count == 0 && wasEnabled)
            {
                ProgramBlock.IsEnabled = true;
            }

            return errors;
        }

        public override MethodRunResult Setup()
        {
            MethodRunResult result = null;
            if (_scriptAssembly != null && CheckAppInstance())
            {
                result = (MethodRunResult) _methodSetup.Invoke(_scriptInstance, null);
                result.ReturnValue = (bool) result.ReturnValue || ProgramBlock.WillRun;
            }
            return result;
        }

        public override MethodRunResult Run(string options)
        {
            MethodRunResult result = null;
            if (_scriptAssembly != null && CheckAppInstance())
            {
                result = (MethodRunResult) _methodRun.Invoke(_scriptInstance, new object[1] {options});
            }
            return result;
        }

        public void Reset()
        {
            if (_scriptAssembly != null && _methodReset != null)
            {
                _methodReset.Invoke(_scriptInstance, null);
            }
        }


        public override ProgramError GetFormattedError(Exception e, bool isTriggerBlock)
        {
            var error = new ProgramError()
            {
                CodeBlock = isTriggerBlock ? CodeBlockEnum.TC : CodeBlockEnum.CR,
                Column = 0,
                Line = 0,
                ErrorNumber = "-1",
                ErrorMessage = e.Message
            };
            // determine error line number
            var st = new StackTrace(e, true);
            var stackFrames = st.GetFrames();
            if (stackFrames != null && stackFrames.Length > 0)
            {
                error.Line = stackFrames[0].GetFileLineNumber();
                foreach (var frame in stackFrames)
                {
                    var declaringType = frame.GetMethod().DeclaringType;
                    if (declaringType != null)
                    {
                        if (declaringType.FullName != null && declaringType.FullName.EndsWith("HomeGenie.Automation.Scripting.ScriptingInstance"))
                        {
                            error.Line = frame.GetFileLineNumber();
                            break;
                        }
                    }
                }
            }
            if (isTriggerBlock)
            {
                var sourceLines = ProgramBlock.ScriptSource.Split('\n').Length;
                error.Line -= (CSharpAppFactory.ConditionCodeOffset + CSharpAppFactory.ProgramCodeOffset + sourceLines);
            }
            else
            {
                error.Line -= CSharpAppFactory.ProgramCodeOffset;
            }
            return error;
        }

        private string AssemblyFile
        {
            get
            {
                var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "programs");
                file = Path.Combine(file, ProgramBlock.Address + ".dll");
                return file;
            }
        }

        private bool LoadAssembly()
        {
            try
            {
                var assemblyData = File.ReadAllBytes(this.AssemblyFile);
                byte[] debugData = null;
                if (File.Exists(this.AssemblyFile + ".mdb"))
                {
                    debugData = File.ReadAllBytes(this.AssemblyFile + ".mdb");
                }
                else if (File.Exists(this.AssemblyFile + ".pdb"))
                {
                    debugData = File.ReadAllBytes(this.AssemblyFile + ".pdb");
                }
                _scriptAssembly = debugData != null
                    ? Assembly.Load(assemblyData, debugData)
                    : Assembly.Load(assemblyData);
                return true;
            }
            catch (Exception e)
            {
                ProgramBlock.ScriptErrors = e.Message + "\n" + e.StackTrace;
                return false;
            }
        }

        private bool CheckAppInstance()
        {
            var success = false;
            if (_programDomain != null)
            {
                success = true;
            }
            else
            {
                try
                {
                    // Creating app domain
                    _programDomain = AppDomain.CurrentDomain;

                    _assemblyType = _scriptAssembly.GetType("HomeGenie.Automation.Scripting.ScriptingInstance");
                    _scriptInstance = Activator.CreateInstance(_assemblyType);

                    var miSetHost = _assemblyType.GetMethod("SetHost");
                    miSetHost.Invoke(_scriptInstance, new object[2] {HomeGenie, ProgramBlock.Address});

                    _methodRun = _assemblyType.GetMethod("Run",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    _methodSetup = _assemblyType.GetMethod("Setup",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    _methodReset = _assemblyType.GetMethod("Reset");

                    success = true;
                }
                catch (Exception ex)
                {
                    HomeGenieService.LogError(
                        Domains.HomeAutomation_HomeGenie_Automation,
                        ProgramBlock.Address.ToString(),
                        ex.Message,
                        "Exception.StackTrace",
                        ex.StackTrace
                    );
                }
            }
            return success;
        }
    }
}
