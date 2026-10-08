using ArriOpto.BLE.Agp;
using CommunityToolkit.Mvvm.Input;

namespace ArriLight.ViewModels;

public partial class DeviceControlPageViewModel
{
    private void LoadCustomPresets(IReadOnlyList<SavedControlPreset> savedPresets)
    {
        for (var index = 0; index < Presets.Count; index++)
        {
            var saved = savedPresets.LastOrDefault(preset => preset.Slot == index);
            if (saved is not null && (!Presets[index].IsSaved || Presets[index].ToSaved(index) != saved))
                Presets[index] = ControlPreset.FromSaved(saved);
            else if (saved is null && Presets[index].IsSaved)
                Presets[index] = ControlPreset.Empty();
        }

        ConfigureControlPage();
    }

    private void PersistCustomPresets()
    {
        if (string.IsNullOrWhiteSpace(DeviceAddress))
            return;

        _repository.SetCustomPresets(
            DeviceAddress,
            Presets
                .Select((preset, slot) => preset.IsSaved
                    ? preset.ToSaved(slot)
                    : null)
                .OfType<SavedControlPreset>());
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (!IsDeviceOnline ||
            (!SupportsBrightness && !SupportsColor && !SupportsTemperature && !SupportsEffect))
            return;
        if (Presets.All(preset => preset.IsSaved))
        {
            await _interaction.AlertAsync(AppResources.Preset_Full, AppResources.Preset_MaximumReached, AppResources.Common_Ok);
            return;
        }

        var name = await _interaction.PromptAsync(
            AppResources.Preset_SaveCustom,
            AppResources.Preset_NamePrompt,
            accept: AppResources.Common_Save,
            cancel: AppResources.Common_Cancel,
            placeholder: AppResources.Preset_NameExample,
            maxLength: 12);

        if (string.IsNullOrWhiteSpace(name))
            return;

        var emptyIndex = Presets
            .Select((preset, index) => (preset, index))
            .First(item => !item.preset.IsSaved)
            .index;

        Presets[emptyIndex] = ControlPreset.Create(
            name.Trim(),
            SupportsColor ? Hue : null,
            SupportsColor ? Saturation : null,
            SupportsTemperature ? Temperature : null,
            SupportsBrightness ? Brightness : null,
            SupportsColor ? SelectedColor : null,
            SupportsEffect ? EffectMode : null,
            SupportsEffect && EffectMode != AgpLightMode.Steady
                ? GetEffectPeriod(EffectMode)
                : (ushort?)0);
        PersistCustomPresets();
    }

    [RelayCommand]
    private async Task PresetContextAsync(ControlPreset preset)
    {
        var deleteAction = AppResources.Common_Delete;
        var reorderAction = AppResources.Preset_Reorder;
        var action = await _interaction.ActionSheetAsync(
            preset.DisplayName,
            AppResources.Common_Cancel,
            deleteAction,
            reorderAction);

        if (action == deleteAction)
        {
            var confirmed = await _interaction.ConfirmAsync(
                AppResources.Preset_Delete,
                Localization.Format(nameof(AppResources.Preset_DeleteConfirmation), preset.DisplayName),
                AppResources.Common_Delete,
                AppResources.Common_Cancel);

            var index = Presets.IndexOf(preset);
            if (confirmed && index >= 0)
            {
                Presets[index] = ControlPreset.Empty();
                PersistCustomPresets();
            }
        }
        else if (action == reorderAction)
        {
            SetReorderMode(true);
            await _interaction.AlertAsync(
                AppResources.Preset_Reorder,
                AppResources.Preset_ReorderInstructions,
                AppResources.Preset_StartReordering);
        }
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(ControlPreset preset)
    {
        if (!preset.IsSaved)
        {
            await _interaction.AlertAsync(
                AppResources.Preset_EmptySlot,
                AppResources.Preset_SaveInstructions,
                AppResources.Common_Ok);
            return;
        }

        _lightingEditVersion++;
        _isApplyingControlState = true;
        try
        {
            if (SupportsColor && preset.HasColor)
            {
                Hue = preset.Hue ?? Hue;
                Saturation = preset.Saturation ?? Saturation;
            }

            if (SupportsTemperature && preset.HasTemperature)
                Temperature = preset.Temperature!.Value;

            if (SupportsBrightness && preset.HasBrightness)
                Brightness = preset.Brightness!.Value;
        }
        finally
        {
            _isApplyingControlState = false;
        }

        RenderRequested?.Invoke();
        var hasLightSettings = (SupportsColor && preset.HasColor) ||
            (SupportsTemperature && preset.HasTemperature) ||
            (SupportsBrightness && preset.HasBrightness);
        if (hasLightSettings)
        {
            // Light-model writes can reset a running effect. Send them first,
            // then apply the saved effect and explicitly turn the light on.
            await StopPendingLightSettingsAsync();
            await SendLightSettingsAsync(CancellationToken.None, debounce: false);
        }
        else if (SupportsEffect && preset.EffectMode is not null)
        {
            // A previous slider/color change must not arrive after this effect.
            await StopPendingLightSettingsAsync();
        }

        if (SupportsEffect && preset.EffectMode is { } effectMode)
        {
            var period = preset.EffectPeriodMilliseconds ??
                GetEffectPeriod(effectMode);
            await SetEffectAsync(effectMode, period);
        }
    }

    public void SetReorderMode(bool enabled)
    {
        IsReorderMode = enabled;
        foreach (var preset in Presets) preset.IsReorderMode = enabled;
        RenderRequested?.Invoke();
    }

    public void ReorderPreset(ControlPreset source, ControlPreset target)
    {
        var sourceIndex = Presets.IndexOf(source); var targetIndex = Presets.IndexOf(target);
        if (sourceIndex >= 0 && targetIndex >= 0 && sourceIndex != targetIndex)
        { Presets[sourceIndex] = target; Presets[targetIndex] = source; PersistCustomPresets(); }
        SetReorderMode(false);
    }

    [RelayCommand]
    private void FinishReorder() => SetReorderMode(false);
}
