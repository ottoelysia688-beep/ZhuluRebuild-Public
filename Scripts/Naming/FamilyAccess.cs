using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ZhuluRebuild.Naming
{
    /// <summary>
    /// WorldBox 0.51.x 的“家庭(Family)”访问层。
    ///
    /// Family 与 Clan 是两套对象：Family 表示父母子女组成的家庭，Clan 表示氏族。
    /// 旧版逐鹿只处理 Clan，导致孩子可能没有继承原版 Family。为避免把本 Mod
    /// 绑定死在某个小版本的 Family 字段布局上，这里只通过缓存反射访问。
    /// 任一入口失败都安全返回，不阻断出生流程。
    /// </summary>
    public static class FamilyAccess
    {
        private static readonly Dictionary<Type, MemberInfo> FamilyMemberByActorType = new Dictionary<Type, MemberInfo>();
        private static readonly Dictionary<Type, MethodInfo> SetFamilyByActorType = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MemberInfo> DataMemberByType = new Dictionary<Type, MemberInfo>();
        private static readonly Dictionary<Type, MemberInfo> UnitsMemberByType = new Dictionary<Type, MemberInfo>();

        public static object GetFamily(Actor actor)
        {
            if (actor == null) return null;
            try
            {
                Type type = actor.GetType();
                if (!FamilyMemberByActorType.TryGetValue(type, out MemberInfo member))
                {
                    member = FindPropertyOrField(type, "family")
                        ?? FindPropertyOrField(type, "_family")
                        ?? FindPropertyOrField(type, "<family>k__BackingField");
                    FamilyMemberByActorType[type] = member;
                }
                return ReadMember(member, actor);
            }
            catch { return null; }
        }

        public static bool SetFamily(Actor actor, object family)
        {
            if (actor == null || family == null) return false;
            try
            {
                Type type = actor.GetType();
                if (!SetFamilyByActorType.TryGetValue(type, out MethodInfo method))
                {
                    method = FindOneArgMethod(type, "setFamily", family.GetType())
                        ?? FindOneArgMethod(type, "set_family", family.GetType());
                    SetFamilyByActorType[type] = method;
                }
                if (method == null) return false;
                method.Invoke(actor, new[] { family });
                return ReferenceEquals(GetFamily(actor), family) || GetObjectId(GetFamily(actor)) == GetObjectId(family);
            }
            catch { return false; }
        }

        public static object GetData(object meta)
        {
            if (meta == null) return null;
            try
            {
                Type type = meta.GetType();
                if (!DataMemberByType.TryGetValue(type, out MemberInfo member))
                {
                    member = FindPropertyOrField(type, "data") ?? FindPropertyOrField(type, "_data");
                    DataMemberByType[type] = member;
                }
                return ReadMember(member, meta);
            }
            catch { return null; }
        }

        public static string GetName(object meta)
        {
            object data = GetData(meta);
            return ReadString(data, "name");
        }

        public static bool SetName(object meta, string name)
        {
            if (meta == null || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                MethodInfo setter = FindOneArgMethod(meta.GetType(), "setName", typeof(string));
                if (setter != null)
                {
                    setter.Invoke(meta, new object[] { name });
                    return true;
                }
                object data = GetData(meta);
                return WriteString(data, "name", name);
            }
            catch { return false; }
        }

        public static IEnumerable<Actor> GetMembers(object family)
        {
            if (family == null) yield break;
            object value = null;
            try
            {
                Type type = family.GetType();
                if (!UnitsMemberByType.TryGetValue(type, out MemberInfo member))
                {
                    member = FindPropertyOrField(type, "units") ?? FindPropertyOrField(type, "_units");
                    UnitsMemberByType[type] = member;
                }
                value = ReadMember(member, family);
            }
            catch { }
            if (!(value is IEnumerable enumerable)) yield break;
            foreach (object entry in enumerable)
                if (entry is Actor actor) yield return actor;
        }

        public static long GetObjectId(object value)
        {
            if (value == null) return -1;
            try
            {
                MethodInfo method = FindNoArgMethod(value.GetType(), "getID");
                if (method != null) return Convert.ToInt64(method.Invoke(value, null));
                object id = ReadMember(FindPropertyOrField(value.GetType(), "id"), value);
                return id == null ? -1 : Convert.ToInt64(id);
            }
            catch { return -1; }
        }

        public static long GetFounderActorId(object family)
        {
            object data = GetData(family);
            if (data == null) return -1;
            try
            {
                object value = ReadMember(FindPropertyOrField(data.GetType(), "founder_actor_id"), data);
                return value == null ? -1 : Convert.ToInt64(value);
            }
            catch { return -1; }
        }

        public static string GetFounderActorName(object family)
        {
            return ReadString(GetData(family), "founder_actor_name");
        }

        private static string ReadString(object value, string name)
        {
            if (value == null) return "";
            try { return ReadMember(FindPropertyOrField(value.GetType(), name), value) as string ?? ""; }
            catch { return ""; }
        }

        private static bool WriteString(object value, string name, string text)
        {
            if (value == null) return false;
            try
            {
                MemberInfo member = FindPropertyOrField(value.GetType(), name);
                if (member is PropertyInfo p && p.CanWrite) { p.SetValue(value, text, null); return true; }
                if (member is FieldInfo f) { f.SetValue(value, text); return true; }
            }
            catch { }
            return false;
        }

        private static object ReadMember(MemberInfo member, object instance)
        {
            if (member is PropertyInfo p) return p.GetValue(instance, null);
            if (member is FieldInfo f) return f.GetValue(instance);
            return null;
        }

        private static MemberInfo FindPropertyOrField(Type type, string name)
        {
            while (type != null)
            {
                PropertyInfo p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null) return p;
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                type = type.BaseType;
            }
            return null;
        }

        private static MethodInfo FindNoArgMethod(Type type, string name)
        {
            while (type != null)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, Type.EmptyTypes, null);
                if (method != null) return method;
                type = type.BaseType;
            }
            return null;
        }

        private static MethodInfo FindOneArgMethod(Type type, string name, Type argumentType)
        {
            while (type != null)
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != name) continue;
                    ParameterInfo[] ps = method.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(argumentType)) return method;
                }
                type = type.BaseType;
            }
            return null;
        }
    }
}
