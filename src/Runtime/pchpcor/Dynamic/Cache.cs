﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Pchp.Core.Dynamic
{
    internal static class Cache
    {
        public static class Types
        {
            public static Type[] Empty = new Type[0];
            public static Type[] Int = new Type[] { typeof(int) };
            public static Type[] Long = new Type[] { typeof(long) };
            public static Type[] Double = new Type[] { typeof(double) };
            public static Type[] String = new Type[] { typeof(string) };
            public static Type[] Bool = new Type[] { typeof(bool) };
        }
        
        /// <summary>
        /// Gets method info in given type.
        /// </summary>
        public static MethodInfo GetMethod(this Type type, string name, params Type[] ptypes)
        {
            return type.GetRuntimeMethod(name, ptypes);
        }
    }
}
