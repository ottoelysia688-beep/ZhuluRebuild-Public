using System;
using System.Reflection;

namespace ZhuluRebuild.Naming
{
    /// <summary>
    /// Actor 数据访问辅助类（带缓存）。
    /// WorldBox 0.51.2 中 Actor.data 不是公开字段，需要通过反射访问。
    /// 所有反射结果均缓存，避免热路径重复查找。
    /// </summary>
    public static class ActorAccess
    {
        private static FieldInfo _dataField;
        private static bool _searched;
        private static FieldInfo _nameField;
        private static bool _nameFieldSearched;
        private static FieldInfo _customNameField;
        private static bool _customNameFieldSearched;
        private static FieldInfo _sexField;
        private static bool _sexFieldSearched;
        private static MethodInfo _getMethod;
        private static bool _getMethodSearched;
        private static MethodInfo _setMethod;
        private static bool _setMethodSearched;
        private static MethodInfo _setCityMethod;
        private static bool _setCitySearched;

        private static FieldInfo DataField
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    Type type = typeof(Actor);
                    while (type != null)
                    {
                        _dataField = type.GetField("data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_dataField != null) break;
                        type = type.BaseType;
                    }
                }
                return _dataField;
            }
        }

        public static object GetData(Actor actor)
        {
            if (actor == null) return null;
            var field = DataField;
            if (field == null) return null;
            try { return field.GetValue(actor); } catch { return null; }
        }

        public static string GetName(Actor actor)
        {
            if (actor == null) return "";
            try { return actor.getName() ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// 直接读取 ActorData.name，不经过 Actor.getName 的 Harmony 后置。
        /// 用于识别玩家在原版编辑器中做出的真实改名，避免把逐鹿的显示称号
        /// （皇帝/公王等）误当成数据名，也避免 getName 递归。
        /// </summary>
        public static string GetRawName(Actor actor)
        {
            if (actor == null) return "";
            var data = GetData(actor);
            if (data == null) return "";
            try
            {
                if (!_nameFieldSearched)
                {
                    _nameFieldSearched = true;
                    _nameField = FindField(data.GetType(), "name");
                }
                return _nameField?.GetValue(data) as string ?? "";
            }
            catch { return ""; }
        }

        public static void SetName(Actor actor, string name)
        {
            if (actor == null || string.IsNullOrWhiteSpace(name)) return;
            var data = GetData(actor);
            if (data == null) return;
            try
            {
                if (!_nameFieldSearched)
                {
                    _nameFieldSearched = true;
                    _nameField = FindField(data.GetType(), "name");
                }
                if (_nameField != null) _nameField.SetValue(data, name);
            }
            catch { }
        }

        /// <summary>
        /// 原版人物编辑器在调用 setName(text) 前会把 BaseSystemData.custom_name 设为 true。
        /// 用该标记区分玩家显式改名与原版初始自动命名，避免把新生角色误标为手动名。
        /// </summary>
        public static bool IsCustomName(Actor actor)
        {
            var data = GetData(actor);
            if (data == null) return false;
            try
            {
                if (!_customNameFieldSearched)
                {
                    _customNameFieldSearched = true;
                    _customNameField = FindField(data.GetType(), "custom_name");
                }
                return _customNameField != null && _customNameField.GetValue(data) is bool value && value;
            }
            catch { return false; }
        }

        /// <summary>判断名字是否为空（直接读字段，不调用 getName() 避免触发 Harmony 补丁递归）。</summary>
        public static bool IsNameEmpty(Actor actor)
        {
            if (actor == null) return true;
            var data = GetData(actor);
            if (data == null) return true;
            try
            {
                if (!_nameFieldSearched)
                {
                    _nameFieldSearched = true;
                    _nameField = FindField(data.GetType(), "name");
                }
                if (_nameField == null) return true;
                string name = _nameField.GetValue(data) as string;
                return string.IsNullOrWhiteSpace(name);
            }
            catch { return true; }
        }

        public static ActorSex GetSex(Actor actor)
        {
            var data = GetData(actor);
            if (data == null) return ActorSex.Male;
            try
            {
                if (!_sexFieldSearched)
                {
                    _sexFieldSearched = true;
                    _sexField = FindField(data.GetType(), "sex");
                }
                if (_sexField != null) return (ActorSex)_sexField.GetValue(data);
            }
            catch { }
            return ActorSex.Male;
        }

        public static string GetString(Actor actor, string key)
        {
            if (actor == null || string.IsNullOrEmpty(key)) return "";
            var data = GetData(actor);
            if (data == null) return "";
            try
            {
                if (!_getMethodSearched)
                {
                    _getMethodSearched = true;
                    _getMethod = data.GetType().GetMethod("get", new[] { typeof(string), typeof(string).MakeByRefType(), typeof(string) });
                }
                if (_getMethod != null)
                {
                    object[] args = { key, null, "" };
                    _getMethod.Invoke(data, args);
                    return args[1] as string ?? "";
                }
            }
            catch { }
            return "";
        }

        public static void SetString(Actor actor, string key, string value)
        {
            if (actor == null || string.IsNullOrEmpty(key)) return;
            var data = GetData(actor);
            if (data == null) return;
            try
            {
                if (!_setMethodSearched)
                {
                    _setMethodSearched = true;
                    _setMethod = data.GetType().GetMethod("set", new[] { typeof(string), typeof(string) });
                }
                if (_setMethod != null)
                {
                    _setMethod.Invoke(data, new object[] { key, value ?? "" });
                }
            }
            catch { }
        }

        /// <summary>
        /// 2026-08-19 迁居：调用 Actor.setCity(City)（非公开方法，内部处理新旧城市簿记）。
        /// 反射缓存；失败时回退直接赋值公共字段 actor.city。
        /// </summary>
        public static bool SetHomeCity(Actor actor, City city)
        {
            if (actor == null || city == null) return false;
            try
            {
                if (!_setCitySearched)
                {
                    _setCitySearched = true;
                    Type t = typeof(Actor);
                    while (t != null && _setCityMethod == null)
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.Name != "setCity") continue;
                            var ps = m.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(City)) { _setCityMethod = m; break; }
                        }
                        t = t.BaseType;
                    }
                }
                if (_setCityMethod != null)
                {
                    _setCityMethod.Invoke(actor, new object[] { city });
                    return actor.city == city;
                }
                // 回退：直接赋值公共字段（无城市簿记，聊胜于无）
                actor.city = city;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 按名字解析实例字段，自动兼容「自动属性后备字段」。
        ///
        /// 2026-10-23 重大修正：BaseSystemData 的 name / custom_name / sex 都是**自动属性**，
        /// 真字段名为 `&lt;name&gt;k__BackingField`，根本不存在叫 name 的字段。
        /// 旧实现只按字段名找 → 恒返回 null，导致：
        ///   · IsCustomName 恒 false → 玩家改名补丁被守卫挡住（改名后又被权威名覆盖回去）
        ///   · IsNameEmpty 恒 true  → 新老角色一律当成"无名"处理
        ///   · GetRawName 恒空      → 外部改名检测失效
        ///   · SetName 变成空操作
        /// 这些都不报错，是典型的"反射静默失败"。
        /// </summary>
        private static FieldInfo FindField(Type type, string name)
        {
            string backing = "<" + name + ">k__BackingField";
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;

                // 自动属性：直接读写后备字段，避开属性访问器（防止触发 Harmony 补丁造成递归）
                FieldInfo autoField = type.GetField(backing, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (autoField != null) return autoField;

                type = type.BaseType;
            }
            return null;
        }

        /// <summary>供自检/单测使用：解析字段（含自动属性后备字段）。</summary>
        public static FieldInfo ResolveField(Type type, string name) => FindField(type, name);
    }
}
