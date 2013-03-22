//
// https://github.com/omgtehlion/NodeVsDebugger
// NodeVsDebugger: Node.js Debugging Support for Visual Studio.
//
// Authors:
//   Anton A. Drachev (anton@drachev.com)
//
// Copyright © 2013
//
// Licensed under the terms of BSD 2-Clause License.
// See a license.txt file for the full text of the license.
//

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace NodeVsDebugger
{
    public sealed class DebuggedProcess
    {
        public readonly int Id;
        public readonly string Name;
        public V8DebugSession dbg;
        Process proc;
        EngineCallback Callback;
        WorkerThread m_pollThread;
        string dbgHost = "localhost";
        int dbgPort = 5858;
        bool dbgConnectOnly = false;
        string dbgWorkDir;
        string nodeExe;
        string main;
        ManualResetEvent attachEvent;
        volatile bool attached = false;
        TempScriptCache tempScriptCache;
        ScriptMapping mappings;

        public DebuggedProcess(string exe, string args, WorkerThread pollThread, EngineCallback callback)
        {
            Callback = callback;
            m_pollThread = pollThread;
            tempScriptCache = new TempScriptCache();
            main = exe;
            dbgWorkDir = Path.GetDirectoryName(exe);
            ParseConfig(args);

            proc = new Process();
            if (!dbgConnectOnly) {
                if (nodeExe == null || !File.Exists(nodeExe)) {
                    System.Windows.Forms.MessageBox.Show("ERROR: node.exe not found.\r\n\r\n" +
                        "Please make sure it is on your %PATH% or installed in default location.\r\n\r\n" +
                        "You can download a copy of Node at http://nodejs.org/", "NodeVsDebugger");
                    throw new ArgumentException("node.exe not found");
                }

                proc.StartInfo = new ProcessStartInfo {
                    Arguments = string.Format("--debug-brk={0} {1}", dbgPort, main),
                    FileName = nodeExe,
                    UseShellExecute = false,
                    WorkingDirectory = dbgWorkDir,
                };
            } else {
                // using fake process and connecting to another machine
                proc.StartInfo = new ProcessStartInfo {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"cmd.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            try {
                proc.Start();
                Id = proc.Id;
            } catch (Exception ex) {
                System.Windows.Forms.MessageBox.Show("ERROR: starting process\r\n" + ex, "NodeVsDebugger");
                throw;
            }
        }

        public void Attach()
        {
            try {
                dbg = new V8DebugSession(dbgHost, dbgPort);
                dbg.Connected += dbg_Connected;
                dbg.Closed += dbg_Closed;
                dbg.StartReading();
            } catch (Exception ex) {
                System.Windows.Forms.MessageBox.Show("ERROR connecting to debuggee:\r\n" + ex, "NodeVsDebugger");
                throw;
            }
        }

        private void ParseConfig(string confText)
        {
            if (confText == null) {
                mappings = new ScriptMapping();
                nodeExe = Tools.GetDefaultNode();
                dbgPort = RandomizePort();
                return;
            }
            var conf = JsonConvert.DeserializeObject(confText) as JObject;
            if (conf == null)
                throw new ArgumentException("cannot deserialize config");

            if (conf["port"] != null)
                dbgPort = (int)conf["port"];

            var mappingConf = conf["mappings"] as JObject;
            if (mappingConf != null)
                mappings = new ScriptMapping(mappingConf);

            switch ((string)conf["mode"]) {
                case null:
                case "run":
                    main = (string)conf["main"] ?? main;
                    nodeExe = (string)conf["node"] ?? Tools.GetDefaultNode();
                    dbgHost = "localhost";
                    break;
                case "connect":
                    dbgConnectOnly = true;
                    dbgHost = (string)conf["host"] ?? dbgHost;
                    break;
                default:
                    throw new ArgumentException("mode = " + (string)conf["mode"]);
            }
        }

        private int RandomizePort()
        {
            var port = 0;
            // node.js requires debug_port > 1024 && debug_port < 65536
            while (port <= 1024 || port >= 65536) {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
            }
            return port;
        }

        void dbg_Closed()
        {
            m_pollThread.RunOperation(() => Callback.OnProcessExit(0));
            Cleanup();
        }

        private void Cleanup()
        {
            if (proc != null && !proc.HasExited)
                proc.Kill();
            if (tempScriptCache != null)
                tempScriptCache.Cleanup();
        }

        void dbg_Connected(Dictionary<string, string> obj)
        {
            attached = true;
            if (attachEvent != null)
                attachEvent.Set();
            dbg.EventReceived += dbg_EventReceived;
            dbg.Request("setexceptionbreak", new { type = "uncaught", enabled = true });
            //dbg.Request("setexceptionbreak", new { type = "all", enabled = true });
        }

        void dbg_EventReceived(string evt, JToken body)
        {
            switch (evt) {
                case "break":
                case "exception":
                    m_pollThread.RunOperation(() => Callback.OnAsyncBreakComplete(Threads[0]));
                    break;
                case "afterCompile":
                    var mod = JsonToScript((JObject)body["script"]);
                    Callback.OnModuleLoad(mod);
                    break;
            }
        }

        public void Break()
        {
            Callback.OnAsyncBreakComplete(Threads[0]);
        }
        public void Continue(DebuggedThread thread)
        {
            dbg.Request("continue", "");
        }

        public void Detach()
        {
            Callback.OnProgramDestroy(0);
            dbg.Request("disconnect", "");
            dbg.Close();
        }
        public void Execute(DebuggedThread thread)
        {
            dbg.Request("continue", "");
        }
        public List<NodeScript> Modules = new List<NodeScript>();
        public IReadOnlyCollection<NodeScript> GetModules()
        {
            return Modules;
        }
        public List<DebuggedThread> Threads = new List<DebuggedThread>();
        public DebuggedThread[] GetThreads()
        {
            return Threads.ToArray();
        }
        public void ResumeFromLaunch()
        {
            dbg.Request("continue", "");
        }
        public void Terminate()
        {
            dbg.RequestSync("evaluate", new { expression = "process.exit(1);", global = true }, 500);
            Detach();
        }

        internal void DoStackWalk(DebuggedThread debuggedThread)
        {
            debuggedThread.StackFrames = new List<NodeThreadContext>();
            var frameId = 0;
            var totalFrames = int.MaxValue;
            var maxTicks = DateTime.Now.AddSeconds(5).Ticks;
            while (frameId < totalFrames && DateTime.Now.Ticks < maxTicks) {
                if (!FetchFrames(debuggedThread, frameId, 5, ref totalFrames))
                    break;
                frameId += 5;
            }
        }

        private bool FetchFrames(DebuggedThread debuggedThread, int fromFrame, int count, ref int totalFrames)
        {
            // we have a problem here: http://code.google.com/p/v8/issues/detail?id=1705
            // better use inlineRefs = true (https://github.com/joyent/node/pull/2379/files)
            var resp = dbg.RequestSync("backtrace", new { fromFrame, toFrame = fromFrame + count, inlineRefs = true }, 1000);
            if (resp == null)
                return false;
            totalFrames = (int)resp["body"]["totalFrames"];
            var frames = (JArray)resp["body"]["frames"];
            if (frames != null)
                debuggedThread.StackFrames.AddRange(frames.Select(x => new NodeThreadContext((JObject)x, this)));
            return true;
        }

        internal void Step(DebuggedThread debuggedThread, Microsoft.VisualStudio.Debugger.Interop.enum_STEPKIND sk)
        {
            var stepaction = "next";
            switch (sk) {
                case Microsoft.VisualStudio.Debugger.Interop.enum_STEPKIND.STEP_INTO:
                    stepaction = "in";
                    break;
                case Microsoft.VisualStudio.Debugger.Interop.enum_STEPKIND.STEP_OUT:
                    stepaction = "out";
                    break;
            }
            dbg.Request("continue", new { stepaction, stepcount = 1 });
        }

        internal Property Evaluate(int frameId, string code, out string error)
        {
            var result = dbg.RequestSync("evaluate", new { expression = code, frame = frameId });
            if ((bool)result["success"]) {
                error = null;
                var fakeVar = new JObject(new JProperty("name", code), new JProperty("value", result["body"]));
                return new Property(fakeVar, this, frameId);
            }
            error = (string)result["message"];
            return null;
        }

        internal int SetBreakpoint(string documentName, uint line, uint column)
        {
            TranslateSourceToGenerated(ref documentName, ref line, ref column);
            var resp = dbg.RequestSync("setbreakpoint", new {
                type = "script",
                target = documentName,
                line,
                column,
                enabled = true,
            });
            if ((bool)resp["success"]) {
                return (int)resp["body"]["breakpoint"];
            }
            return -1;
        }

        public void TranslateSourceToGenerated(ref string documentName, ref uint line, ref uint column)
        {
            string docName = documentName;
            var m = Modules.FirstOrDefault(x => x.LocalFile == docName || x.SourceMap != null && x.SourceMap.SourceFiles.Contains(docName));
            if (m != null)
            {
                if (m.LocalFile != documentName && m.SourceMap != null)
                    m.SourceMap.TranslateSourceToGenerated(ref documentName, ref line, ref column);
                else
                    documentName = m.Name;
            }

            var remoteName = mappings.ToRemote(documentName);
            if (remoteName != null)
                documentName = remoteName;
        }

        public void TranslateGeneratedToSource(NodeScript generatedScript, out string filename, ref uint line, ref uint column)
        {
            if (generatedScript.SourceMap != null)
                generatedScript.SourceMap.TranslateGeneratedToSource(out filename, ref line, ref column);
            else
                filename = generatedScript.Name;
        }

        public void RemoveBreakpoint(int breakpointId)
        {
            dbg.RequestSync("clearbreakpoint", new { breakpoint = breakpointId });
        }

        internal void WaitForAttach()
        {
            attachEvent = new ManualResetEvent(false);
            if (attached) {
                attachEvent = null;
                return;
            }
            if (!attachEvent.WaitOne(10 * 1000))
                throw new Exception("cannot attach");
        }

        internal JObject LookupRef(JToken jToken, int timeoutMs = 600)
        {
            var refId = jToken["ref"];
            if (refId == null)
                return (JObject)jToken;
            return dbg.LookupRef((int)jToken["ref"], timeoutMs) ?? (JObject)jToken;
        }

        internal NodeScript JsonToScript(JObject jObject)
        {
            var id = (int)jObject["id"];
            var name = (string)jObject["name"];
            var script = Modules.FirstOrDefault(m => m.Id == id && m.Name == name);
            if (script == null)
            {
                script = AddModule(id, name);
            }
            return script;
        }

        NodeScript AddModule(int id, string name)
        {
            // First resolve by explicit mapping
            string local = mappings.ToLocal(name);

            // Then try to access the file itself
            if (local == null && File.Exists(name))
                local = name;

            // If anything else failed, just get the source code from node.js itself
            if (local == null || !File.Exists(local))
            {
                var evt = new ManualResetEvent(false);
                dbg.Request("scripts", new { types = 7, ids = new[] { id }, includeSource = true }, resp =>
                {
                    var bodies = (JArray)resp["body"];
                    if (bodies.Any())
                    {
                        var body = bodies[0];
                        local = tempScriptCache.SaveScript(id, name, (string)body["source"]);
                    }
                    evt.Set();
                });
                evt.WaitOne(200);
            }

            SourceMap sourceMapping = null;
            if (local != null && File.Exists(local))
            {
                try
                {
                    sourceMapping = this.GetSourceMap(local);
                }
                catch
                {
                }
            }

            NodeScript script = new NodeScript(id, name, local, sourceMapping);
            Modules.Add(script);
            return script;
        }

        SourceMap GetSourceMap(string file)
        {
            string[] lines = File.ReadAllLines(file);
            int idx = lines.Length - 1;
            int minIdx = Math.Max(lines.Length - 6, 0);
            while (idx >= minIdx && string.IsNullOrWhiteSpace(lines[idx]))
                --idx;

            if (idx < minIdx)
                return null;

            //@ sourceMappingURL=subscriptions.js.map

            const string prefix = "//@ sourceMappingURL=";

            var line = lines[idx];
            if (!line.StartsWith(prefix))
                return null;

            string mapFileName = line.Substring(prefix.Length);
            if (!Path.IsPathRooted(mapFileName))
                mapFileName = Path.Combine(Path.GetDirectoryName(file), mapFileName);
            string mapFileFolder = Path.GetDirectoryName(mapFileName);

            string mappingSource = File.ReadAllText(mapFileName, System.Text.Encoding.ASCII);

            JObject jMapping = JsonConvert.DeserializeObject(mappingSource) as JObject;
            if (int.Parse(jMapping["version"].ToString()) != 3)
                return null;

            string root = (string)jMapping["sourceRoot"];
            var sources = jMapping["sources"].Select(tok => (string)tok);
            if (!string.IsNullOrWhiteSpace(root))
                sources = sources.Select(str => Path.IsPathRooted(str) ? str : Path.Combine(root, str));

            sources = sources.Select(str => Path.IsPathRooted(str) ? str : Path.Combine(mapFileFolder, str));

            string data = (string)jMapping["mappings"];

            return SourceMap.ReadSourceMaps(file, sources.ToArray(), data);
        }
    }
}
