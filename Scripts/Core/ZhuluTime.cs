using UnityEngine;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 统一的逐鹿时间出口。
    /// 权威年份 API：Date.getCurrentYear()（public static，0.51.2 反编译验证）。
    /// 注意：MapBox.map_stats 是 internal，getCurWorldTime()/365f 是错误换算，都不要用。
    /// </summary>
    public static class ZhuluTime
    {
        public static int CurrentYear
        {
            get
            {
                try
                {
                    int y = Date.getCurrentYear();
                    return y > 0 ? y : 1;
                }
                catch { }
                return 1;
            }
        }
    }
}
