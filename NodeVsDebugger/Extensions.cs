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

namespace NodeVsDebugger
{
    public static class Extensions
    {
        public static JObject Lookup(this Dictionary<long, JObject> refs, long refHandle)
        {
            JObject obj;
            refs.TryGetValue(refHandle, out obj);
            return obj;
        }

        public static JObject Lookup(this Dictionary<long, JObject> refs, JToken refContainer)
        {
            var refToken = refContainer["ref"];
            if (refToken != null)
                return Lookup(refs, (long)refToken);
            return (JObject)refContainer;
        }
    }
}
