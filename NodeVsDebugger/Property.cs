﻿//
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

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NodeVsDebugger
{
    public class Property
    {
        public string m_name;
        public string m_typeName;
        public string m_value;

        public int FullStringLength;
        string m_fullString;
        public int m_valueHandle;
        private JObject jObject;
        DebuggedProcess proc;
        PropertyAttribute Attributes;
        PropertyType Types;
        public string m_fullName;

        public bool IsString { get { return m_typeName == "string"; } }
        public bool IsMethod { get { return m_typeName == "function"; } }
        public bool HasChildren { get { return m_typeName == "object" || m_typeName == "function" || m_typeName == "regexp"; } }
        public bool IsPrivate { get { return Attributes.HasFlag(PropertyAttribute.DontEnum); } }
        public bool HasAccessor { get { return Types.HasFlag(PropertyType.CALLBACKS) && m_typeName != "function"; } }

        // see: http://code.google.com/p/v8/source/browse/trunk/src/mirror-debugger.js
        [Flags]
        public enum PropertyType
        {
            // Only in slow mode.
            NORMAL = 0,
            // Only in fast mode.
            FIELD = 1,
            CONSTANT_FUNCTION = 2,
            CALLBACKS = 3,
            // Only in lookup results, not in descriptors.
            HANDLER = 4,
            INTERCEPTOR = 5,
            TRANSITION = 6,
            // Only used as a marker in LookupResult.
            NONEXISTENT = 7
        }

        [Flags]
        public enum PropertyAttribute
        {
            None = 0,
            ReadOnly = 1 << 0,
            DontEnum = 1 << 1,
            DontDelete = 1 << 2
        }

        public Property(JObject jObject, DebuggedProcess proc, string parentName = null)
        {
            this.jObject = jObject;
            this.proc = proc;

            m_name = (string)jObject["name"];
            m_fullName = m_name;
            if (parentName != null) {
                m_fullName = parentName;
                if (Tools.IsValidIdentifier(m_name)) {
                    m_fullName += "." + m_name;
                } else {
                    m_fullName += "[" + JsonConvert.SerializeObject(m_name) + "]";
                }
            }

            Attributes = (PropertyAttribute)(int)(jObject["attributes"] ?? new JValue(0));
            Types = (PropertyType)(int)(jObject["propertyType"] ?? new JValue(0));
            //m_name += "(" + Attributes + ") (" + Types + ")";
            var value = jObject["value"] ?? proc.dbg.LookupRef((int)jObject["ref"], 300);
            if (value != null)
                FillValue((JObject)value);
        }

        private void FillValue(JObject val)
        {
            m_valueHandle = (int)(val["ref"] ?? val["handle"]);
            m_typeName = (string)val["type"];
            switch (m_typeName) {
                case "object":
                    m_value = (string)val["text"];
                    break;
                case "null":
                case "undefined":
                    m_value = m_typeName;
                    break;
                case "number":
                case "boolean":
                    m_value = JsonConvert.SerializeObject(val["value"]);
                    break;
                case "string":
                    m_fullString = (string)val["value"];
                    // fetch displayed text:
                    m_value = JsonConvert.SerializeObject((string)(val["text"] ?? val["value"]));
                    DetectStringLength(m_fullString, val);
                    break;
                case "function":
                    m_value = new NodeFunc(val).AnyName;
                    break;
                case "regexp":
                    m_value = (string)val["text"];
                    break;
                default:
                    throw new NotImplementedException("VariableInformation(" + m_typeName);
                //break;
            }
        }

        private void DetectStringLength(string s, JObject val)
        {
            FullStringLength = s.Length;
            var len = val["length"];
            if (len != null) {
                FullStringLength = (int)len;
            } else {
                if (s[s.Length - 1] == ')') {
                    const string prefix = "... (length: ";
                    var idx = s.IndexOf(prefix, s.Length - prefix.Length - 7, StringComparison.Ordinal);
                    if (idx != -1) {
                        var sLen = s.Substring(idx + prefix.Length).TrimEnd(')');
                        int iLen;
                        if (int.TryParse(sLen, out iLen)) {
                            if (iLen > FullStringLength)
                                FullStringLength = iLen;
                        }
                    }
                }
            }
        }

        // TODO: cache this value
        internal IEnumerable<Property> GetChildren()
        {
            var valueData = proc.dbg.LookupRef(m_valueHandle, 600);
            var props = (JArray)valueData["properties"];
            return props.Select(prop => new Property((JObject)prop, proc, m_fullName));
        }

        internal string GetFullString(int maxChars)
        {
            if (FullStringLength > m_fullString.Length) {
                // we have to fetch actual string
                var valueData = proc.dbg.LookupFullString(m_valueHandle);
                FillValue(valueData);
            }
            if (maxChars >= m_fullString.Length)
                return m_fullString;
            return m_fullString.Substring(0, maxChars);
        }
    }
}
