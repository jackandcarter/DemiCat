using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace DemiCatPlugin;

internal sealed class DateTimePicker
{
    private DateTimeOffset _value;
    private DateTime _displayMonth;
    private DateTime _tempDate;
    private int _tempHour;
    private int _tempMinute;
    private bool _tempIsPm;

    public DateTimePicker(DateTimeOffset initial)
    {
        SetValue(initial);
    }

    public DateTimeOffset Value => _value;

    public void SetValue(DateTimeOffset value)
    {
        _value = value;
        var local = value.ToLocalTime();
        _displayMonth = new DateTime(local.Year, local.Month, 1);
        _tempDate = local.Date;
        _tempMinute = local.Minute;
        var hour = local.Hour;
        _tempIsPm = hour >= 12;
        var hour12 = hour % 12;
        if (hour12 == 0)
        {
            hour12 = 12;
        }

        _tempHour = hour12;
    }

    public bool Draw(string idSuffix, out DateTimeOffset newValue)
    {
        var local = _value.ToLocalTime();
        var preview = local.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
        var popupId = $"TimePickerPopup##{idSuffix}";
        newValue = _value;
        var changed = false;

        var avail = ImGui.GetContentRegionAvail();
        var buttonWidth = Math.Max(avail.X, 1f);
        if (ImGui.Button($"{preview}##{idSuffix}", new Vector2(buttonWidth, 0f)))
        {
            SyncTemporaryState();
            ImGui.OpenPopup(popupId);
        }

        if (ImGui.BeginPopupModal(popupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawMonthHeader(idSuffix);
            DrawCalendar(idSuffix);
            ImGui.Spacing();
            DrawTimeInputs(idSuffix);
            ImGui.Spacing();

            if (ImGui.Button($"Confirm##{idSuffix}"))
            {
                ClampTemporaryValues();
                var hour24 = _tempHour % 12;
                if (_tempIsPm)
                {
                    hour24 += 12;
                }
                else if (_tempHour == 12)
                {
                    hour24 = 0;
                }

                var localDateTime = new DateTime(
                    _tempDate.Year,
                    _tempDate.Month,
                    _tempDate.Day,
                    hour24,
                    _tempMinute,
                    0,
                    DateTimeKind.Unspecified);
                var selected = NormalizeLocalTime(localDateTime);
                SetValue(selected);
                newValue = _value;
                changed = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();

            if (ImGui.Button($"Cancel##{idSuffix}"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        return changed;
    }

    public static DateTimeOffset GetDefaultTime()
    {
        var today = DateTime.Today;
        var localNoon = new DateTime(today.Year, today.Month, today.Day, 12, 0, 0, DateTimeKind.Unspecified);
        return NormalizeLocalTime(localNoon);
    }

    public static DateTimeOffset NormalizeLocalTime(DateTime localDateTime)
    {
        var timeZone = TimeZoneInfo.Local;
        var working = localDateTime;

        if (timeZone.IsInvalidTime(working))
        {
            working = working.AddHours(1);
        }

        TimeSpan offset;
        if (timeZone.IsAmbiguousTime(working))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(working);
            offset = offsets.Length > 0 ? offsets[0] : timeZone.GetUtcOffset(working);
        }
        else
        {
            offset = timeZone.GetUtcOffset(working);
        }

        return new DateTimeOffset(working, offset);
    }

    private void SyncTemporaryState()
    {
        SetValue(_value);
    }

    private void DrawMonthHeader(string idSuffix)
    {
        if (ImGui.Button($"<##Prev{idSuffix}"))
        {
            _displayMonth = _displayMonth.AddMonths(-1);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(_displayMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        ImGui.SameLine();
        if (ImGui.Button($">##Next{idSuffix}"))
        {
            _displayMonth = _displayMonth.AddMonths(1);
        }
    }

    private void DrawCalendar(string idSuffix)
    {
        var culture = CultureInfo.CurrentCulture;
        var firstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek;
        var dayNames = culture.DateTimeFormat.AbbreviatedDayNames;
        var headers = new string[7];
        for (var i = 0; i < 7; i++)
        {
            headers[i] = dayNames[((int)firstDayOfWeek + i) % 7];
        }

        if (ImGui.BeginTable($"##Calendar{idSuffix}", 7, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            for (var i = 0; i < 7; i++)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(headers[i]);
            }

            var firstOfMonth = _displayMonth;
            var daysOffset = ((int)_displayMonth.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
            var firstDate = firstOfMonth.AddDays(-daysOffset);

            for (var week = 0; week < 6; week++)
            {
                ImGui.TableNextRow();
                for (var day = 0; day < 7; day++)
                {
                    var cellDate = firstDate.AddDays(week * 7 + day);
                    ImGui.TableNextColumn();

                    var isCurrentMonth = cellDate.Month == _displayMonth.Month;
                    if (!isCurrentMonth)
                    {
                        ImGui.BeginDisabled();
                    }

                    var isSelected = cellDate.Date == _tempDate.Date;
                    if (ImGui.Selectable(cellDate.Day.ToString(CultureInfo.InvariantCulture), isSelected, ImGuiSelectableFlags.DontClosePopups))
                    {
                        _tempDate = cellDate.Date;
                    }

                    if (!isCurrentMonth)
                    {
                        ImGui.EndDisabled();
                    }
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawTimeInputs(string idSuffix)
    {
        ImGui.TextUnformatted("Time");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        if (ImGui.InputInt($"##Hour{idSuffix}", ref _tempHour))
        {
            ClampTemporaryValues();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(":");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(60f);
        if (ImGui.InputInt($"##Minute{idSuffix}", ref _tempMinute))
        {
            ClampTemporaryValues();
        }

        ImGui.SameLine();
        var isPm = _tempIsPm;
        if (ImGui.RadioButton($"AM##{idSuffix}", !isPm))
        {
            _tempIsPm = false;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton($"PM##{idSuffix}", isPm))
        {
            _tempIsPm = true;
        }
    }

    private void ClampTemporaryValues()
    {
        _tempHour = Math.Clamp(_tempHour, 1, 12);
        _tempMinute = Math.Clamp(_tempMinute, 0, 59);
    }
}
