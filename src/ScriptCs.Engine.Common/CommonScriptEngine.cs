using System;
using System.Collections.Generic;
using System.Linq;
using Common.Logging;
using Microsoft.CodeAnalysis.Scripting;
using ScriptCs.Contracts;

namespace ScriptCs.Engine.Common
{
    public abstract class CommonScriptEngine : IScriptEngine
    {
        protected ScriptOptions ScriptOptions;
        private readonly IScriptHostFactory _scriptHostFactory;

        public const string SessionKey = "Session";

        protected CommonScriptEngine(IScriptHostFactory scriptHostFactory, ILog logger)
        {
            ScriptOptions = new ScriptOptions().WithReferences(typeof(ScriptExecutor).Assembly, typeof(Object).Assembly);
            _scriptHostFactory = scriptHostFactory;
            Logger = logger;
        }

        protected ILog Logger { get; private set; }

        public string BaseDirectory
        {
            get { return ScriptOptions.BaseDirectory; }
            set { ScriptOptions = ScriptOptions.WithBaseDirectory(value); }
        }

        public string CacheDirectory { get; set; }

        public string FileName { get; set; }

        public ScriptResult Execute(string code, string[] scriptArgs, AssemblyReferences references, IEnumerable<string> namespaces, ScriptPackSession scriptPackSession)
        {
            if (scriptPackSession == null)
            {
                throw new ArgumentNullException("scriptPackSession");
            }

            if (references == null)
            {
                throw new ArgumentNullException("references");
            }

            Logger.Debug("Starting to create execution components");
            Logger.Debug("Creating script host");

            var executionReferences = new AssemblyReferences(references.Assemblies, references.Paths);
            executionReferences.Union(scriptPackSession.References);

            ScriptResult scriptResult;
            SessionState<ScriptState> sessionState;

            var isFirstExecution = !scriptPackSession.State.ContainsKey(SessionKey);

            var host = _scriptHostFactory.CreateScriptHost(new ScriptPackManager(scriptPackSession.Contexts), scriptArgs);
            Logger.Debug("Creating session");

            var hostType = host.GetType();

            if (isFirstExecution)
            {
                ScriptOptions = ScriptOptions.AddReferences(hostType.Assembly);
                
                var allNamespaces = namespaces.Union(scriptPackSession.Namespaces).Distinct();

                foreach (var reference in executionReferences.Paths)
                {
                    Logger.DebugFormat("Adding reference to {0}", reference);
                    ScriptOptions = ScriptOptions.AddReferences(reference);
                }

                foreach (var assembly in executionReferences.Assemblies)
                {
                    Logger.DebugFormat("Adding reference to {0}", assembly.FullName);
                    ScriptOptions = ScriptOptions.AddReferences(assembly);
                }

                foreach (var @namespace in allNamespaces)
                {
                    Logger.DebugFormat("Importing namespace {0}", @namespace);
                    ScriptOptions = ScriptOptions.AddNamespaces(@namespace);
                }

                sessionState = new SessionState<ScriptState> { References = executionReferences, Namespaces = new HashSet<string>(allNamespaces) };
                scriptPackSession.State[SessionKey] = sessionState;

                scriptResult = Execute(code, host, sessionState);
            }
            else
            {
                Logger.Debug("Reusing existing session");
                sessionState = (SessionState<ScriptState>)scriptPackSession.State[SessionKey];

                if (sessionState.References == null)
                {
                    sessionState.References = new AssemblyReferences();
                }

                if (sessionState.Namespaces == null)
                {
                    sessionState.Namespaces = new HashSet<string>();
                }

                var newReferences = executionReferences.Except(sessionState.References);

                foreach (var reference in newReferences.Paths)
                {
                    Logger.DebugFormat("Adding reference to {0}", reference);
                    ScriptOptions.AddReferences(reference);
                    sessionState.References = sessionState.References.Union(new[] { reference });
                }

                foreach (var assembly in newReferences.Assemblies)
                {
                    Logger.DebugFormat("Adding reference to {0}", assembly.FullName);
                    ScriptOptions.AddReferences(assembly);
                    sessionState.References = sessionState.References.Union(new[] { assembly });
                }

                var newNamespaces = namespaces.Except(sessionState.Namespaces);

                foreach (var @namespace in newNamespaces)
                {
                    Logger.DebugFormat("Importing namespace {0}", @namespace);
                    ScriptOptions.AddNamespaces(@namespace);
                    sessionState.Namespaces.Add(@namespace);
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    return ScriptResult.Empty;
                }

                scriptResult = Execute(code, sessionState.Session, sessionState);
            }

            return scriptResult;

            //todo handle namespace failures
            //https://github.com/dotnet/roslyn/issues/1012
        }

        protected virtual ScriptResult Execute(string code, object globals, SessionState<ScriptState> sessionState)
        {
            try
            {
                Logger.Debug("Starting execution");
                var result = GetScriptState(code, globals);
                Logger.Debug("Finished execution");
                sessionState.Session = result;
                return new ScriptResult(returnValue: result.ReturnValue);
            }
            catch (AggregateException ex)
            {
                return new ScriptResult(executionException: ex.InnerException);
            }
            catch (CompilationErrorException ex)
            {
                return new ScriptResult(compilationException: ex);
            }
            catch (Exception ex)
            {
                return new ScriptResult(executionException: ex);
            }
        }

        protected abstract ScriptState GetScriptState(string code, object globals);

    }
}