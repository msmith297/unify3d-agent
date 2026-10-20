// Page test: prints what the macro sees through Inputs for every input - no change to the model.
// Numbers are SI (m, rad, Pa, degC, kg, N, Pa*s, s; percent 0-100).
string F(double? v) => v == null ? "null" : v.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

var ids = new[]
{
    "mold_material", "mold_grade", "insert_grade", "part_family", "part_grade",
    "shrink_pct", "sg", "viscosity_cp", "mooney", "pot_life_min", "cure_min", "draft_min_deg", "molding_pressure",
    "press_pressure", "press_pressure_is", "press_temp", "press_rated_force", "press_rated_gauge",
    "platen_size", "daylight", "max_plate_mass", "max_plate_size", "clamp_force",
    "cavities", "cycle_life", "show_extra", "extra_note", "must_set",
    "legacy_len", "legacy_angle", "legacy_plain", "legacy_choice", "legacy_flag", "decisions", "notes",
};

foreach (var id in ids)
{
    var size = Inputs.Size(id);
    var row = Inputs.Row(id);
    Print($"{id}: has={Inputs.Has(id)} hidden={Inputs.Hidden(id)} number={F(Inputs.Number(id))} limit={F(Inputs.Limit(id))} " +
          $"text=\"{Inputs.Text(id)}\" table={F(Inputs.Table(id))} edited={Inputs.Edited(id)}" +
          (size == null ? "" : $" size=[{string.Join(", ", size.Select(v => F(v)))}] m") +
          (row == null ? "" : $" row.id={row["id"]}") +
          $" | display: {Inputs.Display(id)}");
}
Print($"faces selected: {Inputs.Selected("faces").Count}; drawing files: {Inputs.Files("drawing").Count}");
