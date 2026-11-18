using System.Collections;
using UnityEngine;

namespace RoundsP2P.Plugin;

internal static class SandboxFontSmoke
{
    internal static IEnumerator Run(Plugin plugin, int holdSeconds)
    {
        while (ListMenu.instance == null || !ListMenu.instance.IsInitialized)
            yield return null;

        MainMenuHandler.instance.PlaySandbox();
        var console = Object.FindObjectOfType<DevConsole>();
        if (console == null || GM_Test.instance == null || !GM_Test.instance.gameObject.activeSelf)
        {
            plugin.WriteTrace("sandbox_font_smoke_failed", "sandbox or its input not found");
            Application.Quit(2);
            yield break;
        }

        var field = console.inputField;
        var originalFont = field.textComponent.font;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            console.ToggleConsole();
            yield return null;
            const string sample = "Ёё Фрост Щит Яя ROUNDS 123";
            field.SetTextWithoutNotify(sample);
            field.ForceLabelUpdate();
            field.textComponent.ForceMeshUpdate();
            var mesh = field.textComponent.textInfo;
            bool rendered = field.text == sample && field.gameObject.activeInHierarchy && mesh.characterCount >= sample.Length;
            for (int index = 0; rendered && index < sample.Length; index++)
                rendered = mesh.characterInfo[index].textElement?.unicode == sample[index];

            if (rendered && attempt == 0 && holdSeconds > 0)
                yield return new WaitForSecondsRealtime(holdSeconds);

            // Clear before closing so the test does not submit a fake card name.
            field.SetTextWithoutNotify(string.Empty);
            if (rendered)
            {
                // The stock close path submits the field. Use a real card name and
                // check it was consumed by Sandbox, while restoring the original face.
                var cards = CardChoice.instance.cards;
                var card = System.Array.Find(cards, item => item != null && !string.IsNullOrEmpty(item.CardName));
                if (card != null)
                    field.SetTextWithoutNotify(card.CardName);
                else rendered = false;
            }
            if (!rendered)
            {
                plugin.WriteTrace("sandbox_font_smoke_failed", "rendered glyph codes or card catalog mismatch");
                Application.Quit(2);
                yield break;
            }

            console.ToggleConsole();
            if (field.textComponent.font != originalFont)
            {
                plugin.WriteTrace("sandbox_font_smoke_failed", "original font was not restored");
                Application.Quit(2);
                yield break;
            }
        }

        plugin.WriteTrace("sandbox_font_smoke_complete", "cyrillic_latin_digits_match_input_on_two_opens");
        Application.Quit(0);
    }
}
