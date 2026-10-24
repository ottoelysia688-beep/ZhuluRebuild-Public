using System;
using System.Reflection;
using ZhuluRebuild.Naming;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-23 反射字段解析守护。
    ///
    /// 真实事故：玩家把「段荣」改成「拜仁」，关窗重开又变回「段荣」。
    /// 根因不是补丁没挂上，而是 ActorAccess.FindField 只按字段名找 ——
    /// 而 BaseSystemData 的 name / custom_name / sex 都是**自动属性**，
    /// 真字段名是 &lt;name&gt;k__BackingField，于是恒返回 null：
    ///   IsCustomName 恒 false → 改名补丁被守卫挡掉 → 改名被权威名覆盖。
    ///
    /// 这类"反射静默失败"不报错、不崩溃，只能靠契约测试拦。
    /// 用本地自动属性类型复现同样的形状，断言解析器能同时处理
    /// 「普通字段」与「自动属性后备字段」两种形态。
    /// </summary>
    public static class ReflectionFieldTests
    {
        private class FakeData
        {
            // 普通字段
            public string plain_field = "";
            // 自动属性（与 BaseSystemData 的 name / custom_name / sex 同形）
            public string name { get; set; } = "";
            public bool custom_name { get; set; }
            public int sex { get; set; }
            // 只读自动属性
            public string readonly_prop { get; } = "x";
        }

        private sealed class DerivedData : FakeData
        {
        }

        public static void Run()
        {
            // 1. 普通字段
            var f1 = ActorAccess.ResolveField(typeof(FakeData), "plain_field");
            TestRunner.AssertTrue(f1 != null, "解析普通字段");

            // 2. 自动属性 → 后备字段（本次事故的核心）
            var f2 = ActorAccess.ResolveField(typeof(FakeData), "name");
            TestRunner.AssertTrue(f2 != null, "解析自动属性 name 的后备字段");
            TestRunner.AssertTrue(f2 != null && f2.Name == "<name>k__BackingField",
                "解析结果指向 <name>k__BackingField");

            var f3 = ActorAccess.ResolveField(typeof(FakeData), "custom_name");
            TestRunner.AssertTrue(f3 != null, "解析自动属性 custom_name 的后备字段");

            var f4 = ActorAccess.ResolveField(typeof(FakeData), "sex");
            TestRunner.AssertTrue(f4 != null, "解析自动属性 sex 的后备字段");

            // 3. 读写闭环：拿到后备字段后必须能真正取值/写值
            var obj = new FakeData();
            if (f3 != null)
            {
                f3.SetValue(obj, true);
                TestRunner.AssertTrue(obj.custom_name, "后备字段写入生效");
                TestRunner.AssertTrue((bool)f3.GetValue(obj), "后备字段读取生效");
            }
            if (f2 != null)
            {
                f2.SetValue(obj, "拜仁");
                TestRunner.AssertEqual("拜仁", obj.name, "后备字段写入 name 生效");
            }

            // 4. 继承链：字段在基类上也要能找到（ActorData : BaseSystemData 的实际形态）
            var f5 = ActorAccess.ResolveField(typeof(DerivedData), "name");
            TestRunner.AssertTrue(f5 != null, "继承类型解析基类自动属性");

            // 5. 不存在的成员必须返回 null（不能抛）
            var f6 = ActorAccess.ResolveField(typeof(FakeData), "not_exists_at_all");
            TestRunner.AssertTrue(f6 == null, "不存在的成员返回 null");
        }
    }
}
