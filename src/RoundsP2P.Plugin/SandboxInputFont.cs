using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoundsP2P.Plugin;

internal sealed class SandboxInputFont : IDisposable
{
    private const string Cyrillic =
        "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя";

    private static SandboxInputFont? _instance;
    private TMP_Text? _label;
    private TMP_FontAsset? _original;
    private Material? _originalMaterial;
    private TMP_FontAsset? _localFont;
    private TMP_FontAsset? _generatedFallback;
    private Font? _systemFont;

    internal void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(DevConsole), nameof(DevConsole.ToggleConsole)),
            prefix: new HarmonyMethod(typeof(SandboxInputFont), nameof(BeforeToggleConsole)));
        _instance = this;
    }

    private static void BeforeToggleConsole(DevConsole __instance)
    {
        var patch = _instance;
        if (patch == null)
            return;

        var field = __instance.inputField;
        var sandbox = GM_Test.instance;
        if (field == null || field.gameObject.activeSelf || sandbox == null || !sandbox.gameObject.activeSelf)
        {
            patch.RestoreLabel();
            return;
        }

        try
        {
            patch.Apply(field.textComponent);
        }
        catch (Exception exception)
        {
            // Leave the stock console usable if the game font cannot be prepared.
            patch.RestoreLabel();
            Plugin.Instance?.WriteTrace("sandbox_font_error", exception.Message);
        }
    }

    private void Apply(TMP_Text label)
    {
        if (label == null || label.font == null)
            return;
        if (_label == label && label.font == _localFont)
            return;

        var primary = label.font;
        if (primary.HasCharacters(Cyrillic, out uint[] _, searchFallbacks: true, tryAddCharacter: false))
            return;

        if (_label != label || _original != primary)
        {
            ReleaseLocalFont();
            TMP_FontAsset? fallback = FindFallback();
            if (fallback == null)
                return;

            _localFont = Object.Instantiate(primary);
            _localFont.name = primary.name + " (ROUNDS P2P sandbox)";
            _localFont.fallbackFontAssetTable = primary.fallbackFontAssetTable == null
                ? new List<TMP_FontAsset>()
                : new List<TMP_FontAsset>(primary.fallbackFontAssetTable);
            _localFont.fallbackFontAssetTable.Add(fallback);
            _label = label;
            _original = primary;
            _originalMaterial = label.fontSharedMaterial;
            Plugin.Instance?.WriteTrace("sandbox_font_ready", $"{primary.name} => {fallback.name}");
        }

        label.font = _localFont;
        if (_originalMaterial != null)
            label.fontSharedMaterial = _originalMaterial;
    }

    private TMP_FontAsset? FindFallback()
    {
        foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            if (asset != null && asset != _localFont && asset.HasCharacters(Cyrillic))
                return asset;

        if (_generatedFallback != null)
            return _generatedFallback;

        _systemFont = Font.CreateDynamicFontFromOSFont("Arial", 32);
        if (_systemFont == null)
            return null;
        _generatedFallback = TMP_FontAsset.CreateFontAsset(_systemFont);
        if (_generatedFallback != null &&
            _generatedFallback.HasCharacters(Cyrillic, out uint[] _, searchFallbacks: false, tryAddCharacter: true))
            return _generatedFallback;

        ReleaseGeneratedFallback();
        return null;
    }

    private void RestoreLabel()
    {
        if (_label != null && _original != null && _label.font == _localFont)
        {
            _label.font = _original;
            if (_originalMaterial != null)
                _label.fontSharedMaterial = _originalMaterial;
        }
    }

    private void ReleaseLocalFont()
    {
        RestoreLabel();
        if (_localFont != null)
            Object.Destroy(_localFont);
        _localFont = null;
        _original = null;
        _originalMaterial = null;
        _label = null;
    }

    private void ReleaseGeneratedFallback()
    {
        if (_generatedFallback != null)
        {
            foreach (var atlas in _generatedFallback.atlasTextures)
                if (atlas != null) Object.Destroy(atlas);
            if (_generatedFallback.material != null)
                Object.Destroy(_generatedFallback.material);
            Object.Destroy(_generatedFallback);
            _generatedFallback = null;
        }
        if (_systemFont != null)
            Object.Destroy(_systemFont);
        _systemFont = null;
    }

    public void Dispose()
    {
        if (_instance == this)
            _instance = null;
        ReleaseLocalFont();
        ReleaseGeneratedFallback();
    }
}
