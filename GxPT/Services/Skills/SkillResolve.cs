using System;
using System.Collections.Generic;

namespace GxPT
{
    // Combines the global default (SkillEnablement) with a conversation's tri-state overrides into the
    // effective enabled set for a turn (design S10/sec.7). The conversation layer wins when present:
    //   feature: conversation override (null = inherit) else global.FeatureOff
    //   skill:   conversation override (null = inherit) else NOT global.IsDisabled(slug)
    // Everything defaults to on, so a skill absent from both layers is enabled. Pure; net48-testable.
    internal static class SkillResolve
    {
        // Is the whole feature on for this conversation? convFeatureOff: null = inherit global.
        public static bool FeatureOn(SkillEnablement global, bool? convFeatureOff)
        {
            if (convFeatureOff.HasValue) return !convFeatureOff.Value;
            return global == null || !global.FeatureOff;
        }

        // Is one skill enabled for this conversation? convOverride: null = inherit global.
        public static bool SkillEnabled(SkillEnablement global, string slug, bool? convOverride)
        {
            if (convOverride.HasValue) return convOverride.Value;
            return global == null || !global.IsDisabled(slug);
        }

        // The subset of discovered skills enabled for this conversation. Empty when the feature is off.
        public static List<Skill> EnabledSkills(IList<Skill> all, SkillEnablement global,
            bool? convFeatureOff, IDictionary<string, bool> convOverrides)
        {
            List<Skill> result = new List<Skill>();
            if (all == null || !FeatureOn(global, convFeatureOff)) return result;

            for (int i = 0; i < all.Count; i++)
            {
                Skill s = all[i];
                if (s == null) continue;
                bool? ov = null;
                bool v;
                if (convOverrides != null && s.Slug != null && convOverrides.TryGetValue(s.Slug, out v))
                    ov = v;
                if (SkillEnabled(global, s.Slug, ov)) result.Add(s);
            }
            return result;
        }
    }
}
