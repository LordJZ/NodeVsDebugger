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

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;

namespace NodeVsDebugger
{
    [DebuggerDisplay("{Id} {Name}")]
    public class NodeScript : IEquatable<NodeScript>
    {
        public readonly string Name;
        public readonly int Id;

        public readonly string LocalFile;
        public readonly SourceMap SourceMap;

        public NodeScript(int id, string name, string local, SourceMap sourceMap)
        {
            this.Id = id;
            this.Name = name;
            this.LocalFile = local;
            this.SourceMap = sourceMap;
        }

        public IEnumerable<string> SourceScripts
        {
            get
            {
                if (this.SourceMap == null || this.SourceMap.SourceFiles.Length == 0) {
                    yield return this.LocalFile;
                    yield break;
                }

                foreach (var scriptName in this.SourceMap.SourceFiles) {
                    yield return scriptName;
                }
            }
        }

        bool IEquatable<NodeScript>.Equals(NodeScript other)
        {
            if (other == null)
                return false;
            return this == other || (Id == other.Id && Name == other.Name);
        }
    }
}
