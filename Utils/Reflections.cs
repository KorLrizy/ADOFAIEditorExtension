using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ADOFAIEditorExtension.Utils
{
    /// <summary>
    /// 字段/属性/方法的反射读写（照 MultiTrackHelper.Utils.Reflections 复制）；
    /// 游戏里 filteredEvents/shownItems/applyUpdateList 等成员是 protected/private，只能这样访问。
    /// </summary>
    public static class Reflections
    {
        private readonly static Dictionary<(Type, string), Delegate> fieldGetters = new Dictionary<(Type, string), Delegate>();
        private readonly static Dictionary<(Type, string), Delegate> fieldSetters = new Dictionary<(Type, string), Delegate>();

        private readonly static Dictionary<(Type, string), Delegate> propertyGetters = new Dictionary<(Type, string), Delegate>();
        private readonly static Dictionary<(Type, string), Delegate> propertySetters = new Dictionary<(Type, string), Delegate>();

        // 负缓存：查过确实没有该字段/属性的 (类型, 名字)，之后直接返回，不再每次跑 AccessTools（它找不到时还会打警告日志）
        private readonly static HashSet<(Type, string)> missingGetters = new HashSet<(Type, string)>();
        private readonly static HashSet<(Type, string)> missingSetters = new HashSet<(Type, string)>();

        // Method() 的 MethodInfo 缓存（未找到也缓存为 null，调用时照旧抛 MissingMethodException）
        private readonly static Dictionary<MethodKey, MethodInfo> methods = new Dictionary<MethodKey, MethodInfo>();

        public static object Get(this Type type, string name, object instance = null)
        {
            if (fieldGetters.TryGetValue((type, name), out Delegate v1))
                return v1.DynamicInvoke(instance);
            if (propertyGetters.TryGetValue((type, name), out Delegate v2))
                return v2.DynamicInvoke(instance);
            if (missingGetters.Contains((type, name)))
                return null;
            FieldInfo field = AccessTools.Field(type, name);
            if (field != null)
                return CreateFieldGetter(type, field).DynamicInvoke(instance);
            PropertyInfo property = AccessTools.Property(type, name);
            if (property != null)
                return CreatePropertyGetter(type, property).DynamicInvoke(instance);
            missingGetters.Add((type, name));
            return null;
        }

        /// <summary>
        /// 泛型读取。成员缺失或值为 null 时返回 default(T)（值类型不再因拆箱 null 而 NRE）；
        /// 值存在但类型不符时照旧抛 InvalidCastException。
        /// </summary>
        public static T Get<T>(this Type type, string name, object instance = null)
        {
            object value = Get(type, name, instance);
            return value == null ? default : (T)value;
        }

        public static object Get(this object instance, string name)
        {
            return instance?.GetType().Get(name, instance);
        }

        public static T Get<T>(this object instance, string name)
        {
            return instance != null ? instance.GetType().Get<T>(name, instance) : default;
        }

        private static Delegate CreateFieldGetter(Type type, FieldInfo field)
        {
            var instanceExp = Expression.Parameter(type, "instance");
            var fieldExp = Expression.Field(field.IsStatic ? null : instanceExp, field);
            var getter = Expression.Lambda(fieldExp, instanceExp).Compile();
            fieldGetters[(type, field.Name)] = getter;
            return getter;
        }

        private static Delegate CreatePropertyGetter(Type type, PropertyInfo property)
        {
            MethodInfo getMethod = property.GetGetMethod(true) ?? throw new ArgumentException("getter does not exist!");
            var instanceExp = Expression.Parameter(type, "instance");
            var methodExp = Expression.Call(getMethod.IsStatic ? null : instanceExp, getMethod);
            var getter = Expression.Lambda(methodExp, instanceExp).Compile();
            propertyGetters[(type, property.Name)] = getter;
            return getter;
        }

        public static void Set(this Type type, string name, object value, object instance = null)
        {
            if (fieldSetters.TryGetValue((type, name), out Delegate v1))
            {
                v1.DynamicInvoke(instance, value);
                return;
            }
            if (propertySetters.TryGetValue((type, name), out Delegate v2))
            {
                v2.DynamicInvoke(instance, value);
                return;
            }
            if (missingSetters.Contains((type, name)))
                return;
            FieldInfo field = AccessTools.Field(type, name);
            if (field != null)
            {
                CreateFieldSetter(type, field).DynamicInvoke(instance, value);
                return;
            }
            PropertyInfo property = AccessTools.Property(type, name);
            if (property != null)
            {
                CreatePropertySetter(type, property).DynamicInvoke(instance, value);
                return;
            }
            missingSetters.Add((type, name));
        }

        public static void Set(this object instance, string name, object value)
        {
            instance?.GetType().Set(name, value, instance);
        }

        private static Delegate CreateFieldSetter(Type type, FieldInfo field)
        {
            var instanceExp = Expression.Parameter(type, "instance");
            var valueExp = Expression.Parameter(field.FieldType, "value");
            var fieldExp = Expression.Field(field.IsStatic ? null : instanceExp, field);
            var assignExp = Expression.Assign(fieldExp, valueExp);
            var setter = Expression.Lambda(assignExp, instanceExp, valueExp).Compile();
            fieldSetters[(type, field.Name)] = setter;
            return setter;
        }

        private static Delegate CreatePropertySetter(Type type, PropertyInfo property)
        {
            MethodInfo setMethod = property.GetSetMethod(true) ?? throw new ArgumentException("setter does not exist!");
            var instanceExp = Expression.Parameter(type, "instance");
            var valueExp = Expression.Parameter(property.PropertyType, "value");
            var methodExp = Expression.Call(setMethod.IsStatic ? null : instanceExp, setMethod, valueExp);
            var setter = Expression.Lambda(methodExp, instanceExp, valueExp).Compile();
            propertySetters[(type, property.Name)] = setter;
            return setter;
        }

        public static object Method(this Type type, string name, object[] parameters = null, Type[] parameterTypes = null, Type[] genericTypes = null, object instance = null)
        {
            var key = new MethodKey(type, name, parameterTypes, genericTypes);
            if (!methods.TryGetValue(key, out MethodInfo method))
            {
                method = AccessTools.Method(type, name, parameterTypes, genericTypes);
                methods[key] = method;
            }
            if (method == null)
                throw new MissingMethodException(type.FullName + "." + name);
            return method.Invoke(instance, parameters);
        }

        /// <summary>Method() 缓存键：类型 + 方法名 + 参数类型表 + 泛型实参表（数组按元素比较，null 与空表区分开）。</summary>
        private sealed class MethodKey : IEquatable<MethodKey>
        {
            private readonly Type type;
            private readonly string name;
            private readonly Type[] parameterTypes;
            private readonly Type[] genericTypes;
            private readonly int hash;

            internal MethodKey(Type type, string name, Type[] parameterTypes, Type[] genericTypes)
            {
                this.type = type;
                this.name = name;
                // 拷一份，防止调用方之后改动传入的数组导致键变化
                this.parameterTypes = parameterTypes != null ? (Type[])parameterTypes.Clone() : null;
                this.genericTypes = genericTypes != null ? (Type[])genericTypes.Clone() : null;
                int h = (type != null ? type.GetHashCode() : 0) * 31 + (name != null ? name.GetHashCode() : 0);
                h = h * 31 + HashOf(this.parameterTypes);
                h = h * 31 + HashOf(this.genericTypes);
                hash = h;
            }

            private static int HashOf(Type[] types)
            {
                if (types == null)
                    return -1;
                int h = types.Length;
                foreach (Type t in types)
                    h = h * 31 + (t != null ? t.GetHashCode() : 0);
                return h;
            }

            private static bool SameTypes(Type[] a, Type[] b)
            {
                if (a == null || b == null)
                    return a == b;
                return a.SequenceEqual(b);
            }

            public bool Equals(MethodKey other)
            {
                return other != null && type == other.type && name == other.name
                    && SameTypes(parameterTypes, other.parameterTypes) && SameTypes(genericTypes, other.genericTypes);
            }

            public override bool Equals(object obj) => Equals(obj as MethodKey);

            public override int GetHashCode() => hash;
        }

        public static T Method<T>(this Type type, string name, object[] parameters = null, Type[] parameterTypes = null, Type[] genericTypes = null, object instance = null)
        {
            return (T)type.Method(name, parameters, parameterTypes, genericTypes, instance);
        }

        public static object Method(this object instance, string name, object[] parameters = null, Type[] parameterTypes = null, Type[] genericTypes = null)
        {
            return instance?.GetType().Method(name, parameters, parameterTypes, genericTypes, instance);
        }

        public static T Method<T>(this object instance, string name, object[] parameters = null, Type[] parameterTypes = null, Type[] genericTypes = null)
        {
            return instance != null ? instance.GetType().Method<T>(name, parameters, parameterTypes, genericTypes) : default;
        }

        public static Type GetType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name);
                if (type != null)
                    return type;
            }
            return null;
        }
    }
}
