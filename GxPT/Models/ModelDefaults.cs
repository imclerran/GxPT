using System.Collections.Generic;

namespace GxPT
{
    // Single source of truth for the models shipped to NEW installs and the default selection.
    // The seed settings.json (SettingsForm.BuildDefaultJson), the in-memory fallback
    // (SettingsForm.BuildDefaultSettings), and the model dropdown's fresh-install fallback
    // (MainForm.PopulateModelsFromSettings) all pull from here, so the list is defined once and the
    // three can't drift apart. Existing users' configured models live in their own settings.json and
    // are never overwritten by this.
    internal static class ModelDefaults
    {
        // Selected by default on a fresh install. Must be one of Models below.
        public const string DefaultModel = "~anthropic/claude-sonnet-latest";

        // Default catalog, in display order (the model combo is sorted, so order is cosmetic there;
        // it is preserved verbatim in the seeded settings.json).
        public static readonly string[] Models = new string[]
        {
            "~anthropic/claude-opus-latest",
            "~anthropic/claude-sonnet-latest",
            "~anthropic/claude-haiku-latest",
            "~google/gemini-pro-latest",
            "google/gemini-3.5-flash",
            "google/gemma-4-31b-it",
            "openai/gpt-5.4",
            "openai/gpt-5.1-codex-mini",
            "openai/gpt-chat-latest",
            "openai/gpt-5.4-mini",
            "openai/gpt-4o",
            "deepseek/deepseek-v4-pro",
            "deepseek/deepseek-v4-flash",
            "moonshotai/kimi-k2.6",
            "qwen/qwen3.7-max"
        };

        // Convenience copy as a List (callers that mutate or store a List).
        public static List<string> ModelList()
        {
            return new List<string>(Models);
        }
    }
}
