using System;

namespace GammaPanelX
{
    /// <summary>
    /// 由 亮度/对比度/伽马 三个参数生成 256 级 Gamma LUT。
    /// 公式与原版 Gamma Panel 同族：
    ///   v = (i/255)^(1/gamma)            伽马校正
    ///   v = (v-0.5)*((contrast+100)/100)+0.5   以中灰为轴缩放对比度
    ///   v = v + brightness/100*0.5       整体平移亮度
    /// </summary>
    public static class GammaMath
    {
        /// <summary>计算单通道 0..255 输入对应的 0..1 输出。</summary>
        public static double Evaluate(ChannelSettings cs, int i)
        {
            double gamma = cs.Gamma;
            if (gamma < 0.01) gamma = 0.01;
            double v = i / 255.0;
            v = Math.Pow(v, 1.0 / gamma);
            v = (v - 0.5) * ((cs.Contrast + 100.0) / 100.0) + 0.5;
            v = v + cs.Brightness / 100.0 * 0.5;
            if (v < 0.0) v = 0.0;
            if (v > 1.0) v = 1.0;
            return v;
        }

        /// <summary>生成 SetDeviceGammaRamp 所需的 768 项（R/G/B 各 256）查找表。
        /// baseline 非空时, 调节曲线与基线 LUT 复合 (ramp[i] = baseline[curve(i)]),
        /// 这样默认参数精确还原基线, 不会破坏系统已加载的 ICC/vcgt 校准。</summary>
        public static ushort[] BuildRamp(MonitorConfig cfg, ushort[] baseline)
        {
            if (baseline != null && baseline.Length != 768)
                baseline = null;
            ushort[] ramp = new ushort[768];
            FillChannel(ramp, 0, cfg.Red, baseline);
            FillChannel(ramp, 256, cfg.Green, baseline);
            FillChannel(ramp, 512, cfg.Blue, baseline);
            return ramp;
        }

        private static void FillChannel(ushort[] ramp, int offset, ChannelSettings cs, ushort[] baseline)
        {
            for (int i = 0; i < 256; i++)
            {
                double v = Evaluate(cs, i);
                double val;
                if (baseline == null)
                {
                    val = v * 65535.0;
                }
                else
                {
                    // 在基线 LUT 上线性插值采样
                    double pos = v * 255.0;
                    int i0 = (int)pos;
                    if (i0 > 254) i0 = 254;
                    double frac = pos - i0;
                    val = baseline[offset + i0] * (1.0 - frac) + baseline[offset + i0 + 1] * frac;
                }
                if (val < 0.0) val = 0.0;
                if (val > 65535.0) val = 65535.0;
                ramp[offset + i] = (ushort)Math.Round(val);
            }
            // 保证单调不减：部分驱动会拒绝非单调的 LUT
            for (int i = 1; i < 256; i++)
            {
                if (ramp[offset + i] < ramp[offset + i - 1])
                    ramp[offset + i] = ramp[offset + i - 1];
            }
        }
    }
}
