using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using PREACT.Evacuation;
using PREACT.Math;

namespace PREACT.Input
{
    /// <summary>
    /// Writes a <see cref="PREACTInput"/> back out in the <c>.wui</c> format its parsers read.
    ///
    /// The reader is a set of hand-written per-category parsers that look their values up by
    /// <c>nameof(Field)</c>. Rather than mirror each of them by hand - which would be a second
    /// place to forget a field whenever one is added - the key/value body of each section is
    /// emitted by reflecting over the same public members those parsers name. Section structure
    /// (which headers exist, in what order, and which module sub-section belongs to which module)
    /// is explicit, because that part is not derivable from the types.
    ///
    /// Verified by round-tripping: a real case is loaded, written, reloaded, and the two parsed
    /// inputs compared field by field.
    /// </summary>
    public static class PREACTInputWriter
    {
        public static string[] Write(PREACTInput input)
        {
            var lines = new List<string>();

            Section(lines, nameof(PREACTInput.Simulation), input.Simulation);
            Section(lines, nameof(PREACTInput.Map), input.Map);
            Section(lines, nameof(PREACTInput.Landscape), input.Landscape);
            Section(lines, nameof(PREACTInput.Population), input.Population);

            //Demographics belong to the population section and are read from their own repeated
            //headers, so they follow it.
            if (input.Population != null)
            {
                foreach (KeyValuePair<string, DemographicsInput> kv in input.Population.Demographics)
                {
                    Section(lines, "Demographics", kv.Value);
                }
            }

            Section(lines, nameof(PREACTInput.Evacuation), input.Evacuation);
            if (input.Evacuation != null)
            {
                foreach (KeyValuePair<string, ResponseCurve> kv in input.Evacuation.ResponseCurves)
                {
                    ResponseCurveSection(lines, kv.Value);
                }
                foreach (KeyValuePair<string, EvacuationDestinationInput> kv in input.Evacuation.EvacuationDestinationInputs)
                {
                    Section(lines, "Destination", kv.Value);
                }
                foreach (KeyValuePair<string, EvacuationGroupInput> kv in input.Evacuation.EvacuationGroupInputs)
                {
                    Section(lines, "EvacuationGroup", kv.Value);
                }
            }

            Section(lines, nameof(PREACTInput.Events), input.Events);
            Section(lines, nameof(PREACTInput.Weather), input.Weather);

            //Each module is followed by the sub-section named after the module it selected, which
            //is how the parsers locate it (headerLineIndex[nameof(TrafficModules.SUMO)] and so on).
            Section(lines, nameof(PREACTInput.PedestrianModule), input.PedestrianModule);
            ModuleSubSection(lines, input.PedestrianModule, "Module", input.PedestrianModule?.MacroHouseholdSimInput);

            Section(lines, nameof(PREACTInput.TrafficModule), input.TrafficModule);
            ModuleSubSection(lines, input.TrafficModule, "Module", input.TrafficModule?.SumoInput);

            Section(lines, nameof(PREACTInput.WildfireModule), input.WildfireModule);
            ModuleSubSection(lines, input.WildfireModule, "Module", WildfireSubInput(input));

            //The ELMFIRE namelist settings, which Section cannot reach: they hang off ElmfireInput as a
            //nested object and IsWritable refuses those, deliberately - the format has no nesting. Written
            //only for a scenario whose fire is ELMFIRE, since for any other module they configure nothing.
            if (input.WildfireModule?.Module == WildfireModuleInput.WildfireModules.ELMFIRE
                && input.WildfireModule.ElmfireInput?.Namelist != null)
            {
                Section(lines, ElmfireInput.NamelistSection, input.WildfireModule.ElmfireInput.Namelist);
            }

            //Ignition points live on WildfireData, which Section skips along with every other "Data"
            //member, so they are written here explicitly - one repeated section each, the same shape
            //destinations and evacuation groups use.
            if (input.WildfireModule?.Data?.IgnitionPoints != null)
            {
                foreach (Wildfire.IgnitionPointInput point in input.WildfireModule.Data.IgnitionPoints)
                {
                    IgnitionPointSection(lines, point);
                }
            }

            Section(lines, nameof(PREACTInput.SmokeModule), input.SmokeModule);
            ModuleSubSection(lines, input.SmokeModule, "Module", SmokeSubInput(input));

            Section(lines, nameof(PREACTInput.TriggerBufferModule), input.TriggerBufferModule);
            ModuleSubSection(lines, input.TriggerBufferModule, "Module", TriggerBufferSubInput(input));

            return lines.ToArray();
        }

        /// <summary>
        /// Resolves the sub-input a module selected. Done by name so a module gaining another
        /// option does not silently write the wrong section: the accessor is looked up from the
        /// module's own <c>Module</c> enum value.
        /// </summary>
        private static object WildfireSubInput(PREACTInput input)
        {
            if (input.WildfireModule == null) return null;

            //Both fire modules' sub-inputs follow the naming this matches on - ElmfireInput and
            //AscImportInput - so neither needs a special case. The one that did was the cell-based model,
            //configured by a FireCellInput whose name matched nothing, which is why its section was silently
            //never written; it has since been removed.
            return FindSubInput(input.WildfireModule, input.WildfireModule.Module.ToString());
        }

        private static object SmokeSubInput(PREACTInput input)
        {
            if (input.SmokeModule == null) return null;
            return FindSubInput(input.SmokeModule, input.SmokeModule.Module.ToString());
        }

        private static object TriggerBufferSubInput(PREACTInput input)
        {
            if (input.TriggerBufferModule == null) return null;
            return FindSubInput(input.TriggerBufferModule, input.TriggerBufferModule.Module.ToString());
        }

        /// <summary>
        /// Finds the property on a module holding the sub-input for the selected module, matching
        /// on the module name (e.g. AscImport -> AscImportInput).
        /// </summary>
        private static object FindSubInput(object module, string moduleName)
        {
            Type t = module.GetType();
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                if (!p.Name.Equals(moduleName + "Input", StringComparison.OrdinalIgnoreCase) &&
                    !p.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) continue;

                try { return p.GetValue(module); } catch { return null; }
            }
            return null;
        }

        private static void ModuleSubSection(List<string> lines, object module, string moduleFieldName, object subInput)
        {
            if (module == null || subInput == null) return;

            object moduleValue = ReadMember(module, moduleFieldName);
            if (moduleValue == null) return;

            Section(lines, moduleValue.ToString(), subInput);
        }

        private static object ReadMember(object target, string name)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(target);

            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead) { try { return p.GetValue(target); } catch { return null; } }

            return null;
        }

        /// <summary>
        /// A response curve is the one section that is not purely key/value: its data points are
        /// written as bare "time,probability" rows after the header keys, which is the shape the
        /// parser expects.
        /// </summary>
        private static void ResponseCurveSection(List<string> lines, ResponseCurve curve)
        {
            lines.Add("[ResponseCurve]");
            lines.Add("Name=" + curve.Name);
            lines.Add("TimeInput=" + curve.TimeInput);

            if (curve.DataPoints != null)
            {
                for (int i = 0; i < curve.DataPoints.Length; ++i)
                {
                    ResponseDataPoint p = curve.DataPoints[i];
                    lines.Add(F(p.Time) + "," + F(p.Probability));
                }
            }
            lines.Add(string.Empty);
        }

        /// <summary>
        /// One ignition point. Written by hand rather than reflected over, because only two of the four
        /// fields are input: <c>IgnitionTime</c> and <c>IgnitionDateTime</c> are each derived from the
        /// other, and writing both would let a file disagree with itself about when the fire starts.
        /// </summary>
        private static void IgnitionPointSection(List<string> lines, Wildfire.IgnitionPointInput point)
        {
            lines.Add("[IgnitionPoint]");
            lines.Add("LatLon=" + F(point.LatLon.x) + "," + F(point.LatLon.y));
            lines.Add("AbsoluteTime=" + (point.AbsoluteTime ? "true" : "false"));
            if (point.AbsoluteTime)
            {
                lines.Add("IgnitionDateTime=" + point.IgnitionDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            }
            else
            {
                lines.Add("IgnitionTime=" + F(point.IgnitionTime));
            }
            lines.Add(string.Empty);
        }

        private static void Section(List<string> lines, string header, object source)
        {
            if (source == null) return;

            var body = new List<string>();
            Type t = source.GetType();

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Skip(f.Name, f.FieldType)) continue;
                Emit(body, f.Name, f.GetValue(source));
            }

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (Skip(p.Name, p.PropertyType)) continue;

                object value;
                try { value = p.GetValue(source); } catch { continue; }
                Emit(body, p.Name, value);
            }

            //A section with nothing to say is omitted entirely. The parsers treat a present header
            //as a declaration that the feature is configured and then demand its required keys -
            //an empty [Weather], for instance, fails the load outright with "WeatherFile was not
            //found", where no [Weather] at all simply means no weather. Writing the header
            //unconditionally therefore produced files that could not be read back.
            if (body.Count == 0)
            {
                return;
            }

            lines.Add("[" + header + "]");
            lines.AddRange(body);
            lines.Add(string.Empty);
        }

        /// <summary>
        /// Members that are not part of the file: derived runtime state, the root folder (implied
        /// by where the file is), and anything not expressible as a single value.
        /// </summary>
        private static bool Skip(string name, Type type)
        {
            if (name == "Data" || name == "RootFolder") return true;
            return !IsWritable(type);
        }

        private static bool IsWritable(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)) return true;
            if (t.Name == "Vector2d" || t.Name == "Vector2int" || t.Name == "Vector2") return true;

            //r,g,b, the three components the parser splits on
            if (t.Name == "PREACTColor") return true;

            //arrays and lists of simple values are written comma-separated, matching how the
            //parsers read Destinations, DestinationsCDF, ResponseCurves and the rest
            if (t.IsArray) return IsWritable(t.GetElementType());
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                return IsWritable(t.GetGenericArguments()[0]);
            }

            return false;
        }

        private static void Emit(List<string> body, string name, object value)
        {
            if (value == null) return;

            string text = Format(value);
            if (text == null) return;

            //An unset path written as "Key=" is worse than omitting it: the parsers find the key,
            //try to resolve it as a file and fail the load, whereas an absent optional key is
            //handled gracefully.
            if (text.Length == 0) return;

            body.Add(name + "=" + text);
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case string s: return s;
                case DateTime d: return d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                case double dd: return F(dd);
                case float ff: return F(ff);
                case Vector2d v: return F(v.x) + "," + F(v.y);
                //System.Numerics.Vector2, used for pairs such as WalkingSpeedMinMax
                case System.Numerics.Vector2 v2: return F(v2.X) + "," + F(v2.Y);
                //Vector2int needs stating explicitly even though it is IFormattable, because its
                //own ToString yields "(1, 2)" - parentheses and a space - which none of the
                //parsers read. Without this case it reached the IFormattable fallback below and
                //quietly wrote a value that could not be loaded back.
                case Vector2int vi: return vi.x.ToString(CultureInfo.InvariantCulture) + "," + vi.y.ToString(CultureInfo.InvariantCulture);
                //Three components, which is exactly what the parsers split on; alpha is not part
                //of the format. Kept ahead of the IFormattable fallback so this stays correct if
                //PREACTColor ever gains a ToString of its own.
                case PREACTColor colour: return F(colour.r) + "," + F(colour.g) + "," + F(colour.b);
            }

            if (value is Enum) return value.ToString();
            if (value is IFormattable formattable && !(value is IEnumerable))
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            //Arrays and lists share the comma-separated form. Handled after the scalar cases so a
            //string, which is itself enumerable, never reaches here.
            if (value is IEnumerable sequence)
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) sb.Append(',');
                    sb.Append(Format(item));
                    first = false;
                }
                return sb.ToString();
            }

            return null;
        }

        /// <summary>Round-trippable and culture-independent, so a comma-decimal locale cannot turn
        /// one value into two.</summary>
        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
