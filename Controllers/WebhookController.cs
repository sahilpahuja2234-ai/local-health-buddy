using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace HealthBuddyWebhook.Controllers;

[Route("api/[controller]")]
[ApiController]
public class WebhookController : ControllerBase
{
    // ─────────────────────────────────────────────────────────────
    // SESSION MEMORY — remembers the last known good symptoms/duration
    // per Dialogflow session, independent of context lifespan.
    //
    // WHY THIS EXISTS:
    // The previous fix (GetParameter scanning outputContexts) made the
    // "diagnosis" intent work, because at THAT point in the conversation
    // the symptoms/duration context still happened to be active and
    // included in queryResult.outputContexts.
    //
    // But "medical_report" fires on a LATER turn. By then:
    //   - The original context's lifespan may have ticked down to 0
    //     (especially if a fallback/no-match turn occurred in between —
    //     EVERY turn decrements every active context's lifespan by 1,
    //     including failed matches like "Sorry, could you say that again").
    //   - Dialogflow does not guarantee an expired context's parameters
    //     are still visible in outputContexts.
    //
    // So relying purely on Dialogflow's context propagation across
    // multiple turns is fragile. Instead, the webhook remembers the
    // values itself the moment it sees them (e.g. during "diagnosis"),
    // keyed by the Dialogflow session ID, and reuses them for any later
    // intent (medical_report, visit_doctor) that comes back empty.
    //
    // NOTE: This is an in-memory, single-instance cache — perfectly fine
    // for a local student project running one process. It would need to
    // move to a database or distributed cache (e.g. Redis) for a
    // multi-instance production deployment.
    // ─────────────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<string, (string Symptoms, string Duration)> _sessionMemory = new();

    // Saves the latest non-empty symptoms/duration for this session.
    // Never overwrites a remembered value with a blank one.
    private void RememberSession(string sessionId, string symptoms, string duration)
    {
        // ── FIX (CS1061) ─────────────────────────────────────────────────
        // BEFORE: ... ? current : ("", "");
        // PROBLEM: _sessionMemory's value type is the NAMED tuple
        //          (string Symptoms, string Duration). The fallback
        //          ("", "") is an UNNAMED tuple literal. When a ?: operator
        //          has two branches whose tuple types have different
        //          element names, C# drops the names from the result type
        //          entirely — "existing" becomes plain (string, string),
        //          which only exposes .Item1 / .Item2, not .Symptoms /
        //          .Duration. Hence CS1061 on the next two lines.
        //
        // AFTER: Name the fallback tuple's elements to match exactly,
        //        so both branches share the same named tuple type and
        //        "existing.Symptoms" / "existing.Duration" compile.
        var existing = _sessionMemory.TryGetValue(sessionId, out var current)
            ? current
            : (Symptoms: "", Duration: "");

        _sessionMemory[sessionId] = (
            string.IsNullOrWhiteSpace(symptoms) ? existing.Symptoms : symptoms,
            string.IsNullOrWhiteSpace(duration) ? existing.Duration : duration
        );
    }

    // ─────────────────────────────────────────────────────────────
    // POST /api/webhook  — entry point called by Dialogflow
    // ─────────────────────────────────────────────────────────────
    [HttpPost]
    public IActionResult Post([FromBody] JObject body)
    {
        try
        {
            string intentName = body["queryResult"]?["intent"]?["displayName"]?.ToString() ?? "";

            // "session" is a top-level field in every Dialogflow webhook
            // request, formatted like:
            //   projects/abra-ka-dabra-xhsp/agent/sessions/SESSION_ID
            // It is the SAME value for every turn of one conversation,
            // making it a stable cache key.
            string sessionId = body["session"]?.ToString() ?? "default-session";

            // ── FIX (context lookup) ────────────────────────────────────
            // BEFORE: symptoms/duration were read only from
            //         body["queryResult"]["parameters"].
            //
            // PROBLEM: queryResult.parameters ONLY contains parameters that
            //          belong to the CURRENT matched intent. The "diagnosis"
            //          intent (triggered by "what could it be" /
            //          "what is my diagnosis") does NOT itself define a
            //          "symptoms" or "duration" parameter — those were
            //          captured two turns earlier by the "symptoms" and
            //          "syptoms duration" intents.
            //
            //          Dialogflow DOES carry that data forward, but as
            //          parameters stored inside the ACTIVE OUTPUT CONTEXTS
            //          (e.g. local_doctor-syptomsduration-followup), not
            //          inside queryResult.parameters for the new intent.
            //
            // AFTER: GetParameter() checks queryResult.parameters first
            //        (covers intents where the field IS a direct parameter),
            //        then falls back to scanning every active output context
            //        for a parameter with that name.
            string symptoms = GetParameter(body, "symptoms");
            string duration = GetParameter(body, "duration");

            // ── FIX (cross-turn memory fallback) ────────────────────────
            // PROBLEM: By the time "medical_report" fires (one or more turns
            //          after "diagnosis"), the contexts holding symptoms and
            //          duration may have expired or fallen out of
            //          outputContexts, so GetParameter() above returns "".
            //          That's why the report showed empty Symptoms and
            //          "Duration: Not specified" even though diagnosis,
            //          one turn earlier, had the correct values.
            //
            // AFTER: If either value is empty for THIS turn, fill it in
            //        from what we remembered on a previous turn for the
            //        same session.
            if (string.IsNullOrWhiteSpace(symptoms) || string.IsNullOrWhiteSpace(duration))
            {
                if (_sessionMemory.TryGetValue(sessionId, out var remembered))
                {
                    if (string.IsNullOrWhiteSpace(symptoms)) symptoms = remembered.Symptoms;
                    if (string.IsNullOrWhiteSpace(duration)) duration = remembered.Duration;
                }
            }

            // Save whatever good values we have NOW so later intents
            // (medical_report, visit_doctor) can reuse them.
            RememberSession(sessionId, symptoms, duration);

            Console.WriteLine("=== WEBHOOK HIT ===");
            Console.WriteLine($"Session:  {sessionId}");
            Console.WriteLine($"Intent:   {intentName}");
            Console.WriteLine($"Symptoms: {symptoms}");
            Console.WriteLine($"Duration: {duration}");

            string response = RouteIntent(intentName, symptoms, duration);

            return Ok(new JObject { ["fulfillmentText"] = response });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return Ok(new JObject { ["fulfillmentText"] = "Sorry, something went wrong. Please try again." });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Looks for a parameter value in two places, in order:
    //   1. queryResult.parameters[paramName]
    //      — used when the CURRENT intent defines this parameter itself.
    //   2. queryResult.outputContexts[*].parameters[paramName]
    //      — used when the value was captured by an EARLIER intent and
    //        is being carried forward via Dialogflow context lifespan.
    //
    // Returns "" if not found anywhere, so downstream double.TryParse
    // and string.Contains calls continue to work safely.
    // ─────────────────────────────────────────────────────────────
    private string GetParameter(JObject body, string paramName)
    {
        // 1. Direct parameter on the current intent
        var direct = body["queryResult"]?["parameters"]?[paramName];
        if (direct != null)
        {
            var directValue = direct.Type == JTokenType.Array
                ? string.Join(", ", direct.Select(t => t.ToString()))
                : direct.ToString();

            if (!string.IsNullOrWhiteSpace(directValue))
                return directValue;
        }

        // 2. Search every active output context for this parameter
        var contexts = body["queryResult"]?["outputContexts"] as JArray;
        if (contexts != null)
        {
            foreach (var ctx in contexts)
            {
                var ctxValue = ctx["parameters"]?[paramName];
                if (ctxValue == null) continue;

                var value = ctxValue.Type == JTokenType.Array
                    ? string.Join(", ", ctxValue.Select(t => t.ToString()))
                    : ctxValue.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return "";
    }

    // ─────────────────────────────────────────────────────────────
    // ROUTER — picks the right handler based on intent name
    // ─────────────────────────────────────────────────────────────
    private string RouteIntent(string intent, string symptoms, string duration)
    {
        intent = intent.ToLower();
        symptoms = symptoms.ToLower();
        double.TryParse(duration, out double days);

        if (intent.Contains("diagnosis")) return GetDiagnosis(symptoms, days);
        if (intent.Contains("medical_report")) return GetMedicalReport(symptoms, days);
        if (intent.Contains("visit_doctor")) return GetVisitDoctorAdvice(symptoms, days);

        return "I'm here to help. Please describe your symptoms.";
    }

    // =============================================================
    //  DIAGNOSIS — checks BOTH symptoms AND duration together
    // =============================================================
    private string GetDiagnosis(string s, double days)
    {
        string mild = "🟢 Likely mild";
        string mod = "🟡 Moderate concern";
        string high = "🔴 High urgency";

        // Always emergency — chest / breathing
        if (s.Contains("chest pain") || s.Contains("breathlessness"))
            return "🚨 EMERGENCY — Chest pain or breathlessness can indicate a cardiac or respiratory crisis.\n\n" +
                   "Go to the nearest hospital or call emergency services IMMEDIATELY.\n" +
                   "Do NOT wait or try home remedies.";

        // Fever + Rash
        if (s.Contains("fever") && s.Contains("rash"))
        {
            if (days >= 4)
                return $"{high} — Fever with rash for {(int)days} days may indicate Dengue fever, Chikungunya, or Viral Exanthem.\n\n" +
                       "Visit a doctor TODAY. Request a CBC (platelet count) and Dengue NS1 Antigen test.\n\n" +
                       "Warning signs needing emergency care: bleeding gums, severe abdominal pain, fever above 104°F / 40°C.";
            return $"{mod} — Fever with rash may be early Dengue, an allergic reaction, or a viral infection.\n\n" +
                   "See a doctor within 24 hours. Use Paracetamol only — do NOT take Aspirin or Ibuprofen.\n" +
                   "Monitor for a spreading rash or worsening fever.";
        }

        // Fever + Headache / Body ache
        if (s.Contains("fever") && (s.Contains("headache") || s.Contains("body ache") || s.Contains("bodyache")))
        {
            if (days >= 7)
                return $"{high} — Fever with headache for {(int)days} days is serious and may indicate Typhoid, Dengue, or a bacterial infection.\n\n" +
                       "Please visit a doctor TODAY. Tests needed: CBC, Widal test, Blood culture.\n" +
                       "Do not delay — prolonged fever can be dangerous.";
            if (days >= 4)
                return $"{mod} — Fever with headache for {(int)days} days suggests Influenza or early Typhoid.\n\n" +
                       "Visit a clinic today or tomorrow. Take Paracetamol 500mg every 6 hours (max 4 doses/day). " +
                       "Drink 3+ liters of water or ORS daily.\n\n" +
                       "Avoid self-medicating with antibiotics.";
            return $"{mild} — Fever with headache for {(int)days} day(s) is likely Viral Fever or early flu.\n\n" +
                   "Home care: Take Paracetamol, apply a cool damp cloth on your forehead, drink warm fluids, and rest.\n" +
                   "See a doctor if fever lasts more than 3 days or exceeds 103°F (39.5°C).";
        }

        // Cough + Chest Pain
        if (s.Contains("cough") && s.Contains("chest pain"))
        {
            if (days >= 5)
                return $"{high} — Cough with chest pain for {(int)days} days may indicate Bronchitis, Pneumonia, or Pleuritis.\n\n" +
                       "Visit a doctor TODAY. A Chest X-Ray and CBC are needed.\n" +
                       "Do not ignore worsening breathlessness or blood in sputum.";
            return $"{mod} — Cough with chest pain suggests early Bronchitis or a chest infection.\n\n" +
                   "See a doctor within 1–2 days. Avoid cold drinks, smoking, and dusty environments.\n" +
                   "Steam inhalation can ease the cough in the meantime.";
        }

        // Cough + Cold / Runny Nose
        if (s.Contains("cough") && (s.Contains("cold") || s.Contains("runny nose") || s.Contains("sneezing")))
        {
            if (days >= 10)
                return $"{high} — Cough and cold lasting {(int)days} days may have progressed to Sinusitis or Bronchitis.\n\n" +
                       "Please see a doctor. A nasal swab or Chest X-Ray may be needed. " +
                       "Your doctor may consider antibiotics if a bacterial cause is confirmed.";
            if (days >= 6)
                return $"{mod} — Cough and cold for {(int)days} days suggests the infection is not clearing on its own. " +
                       "This may be Bacterial Sinusitis.\n\n" +
                       "Visit a clinic soon. Mention if you have facial pressure or thick yellow/green mucus.";
            return $"{mild} — Cough and cold for {(int)days} day(s) is typical of a Common Cold or Upper Respiratory Infection.\n\n" +
                   "Usually resolves in 5–7 days. Try steam inhalation, warm saline gargle, and ginger-honey-lemon tea.\n" +
                   "See a doctor if no improvement by day 7.";
        }

        // Nausea / Vomiting + Stomach Pain
        if ((s.Contains("nausea") || s.Contains("vomiting")) && (s.Contains("stomach pain") || s.Contains("abdominal pain")))
        {
            if (days >= 3)
                return $"{high} — Vomiting with stomach pain for {(int)days} days may indicate Gastroenteritis, Food Poisoning, or Appendicitis.\n\n" +
                       "Visit a doctor TODAY. Persistent vomiting leads to dangerous dehydration. IV fluids may be needed.\n\n" +
                       "Warning: If pain is sharp and in the lower-right abdomen — go to emergency (possible Appendicitis).";
            return $"{mild} — Nausea and stomach pain for {(int)days} day(s) is likely Gastroenteritis or mild Food Poisoning.\n\n" +
                   "Sip ORS or coconut water every 15 minutes. Eat the BRAT diet (banana, rice, applesauce, toast). " +
                   "Avoid spicy, oily, and dairy foods for 48 hours.\n" +
                   "See a doctor if vomiting is continuous or blood is present.";
        }

        // Diarrhea + Stomach cramps
        if (s.Contains("diarrhea") && (s.Contains("stomach") || s.Contains("cramps")))
        {
            if (days >= 5)
                return $"{high} — Diarrhea for {(int)days} days may indicate a bacterial or parasitic infection (Giardia, Amoeba) or IBS.\n\n" +
                       "Please see a doctor and provide a stool sample for culture and microscopy.\n" +
                       "Keep drinking ORS to prevent severe dehydration.";
            return $"{mild} — Diarrhea with cramps for {(int)days} day(s) is likely Gastroenteritis or a food-related infection.\n\n" +
                   "Take ORS after every loose motion. Eat banana, plain rice, and curd. Avoid milk and raw vegetables.\n" +
                   "See a doctor immediately if you see blood in stool or feel very weak.";
        }

        // Dizziness / Fatigue / Weakness
        if (s.Contains("dizziness") || s.Contains("fatigue") || s.Contains("weakness"))
        {
            if (days >= 7)
                return $"{high} — Persistent fatigue or weakness for {(int)days} days could indicate Anemia, Hypothyroidism, Vitamin B12 deficiency, or Diabetes.\n\n" +
                       "Please visit a doctor. Tests needed: CBC, Thyroid profile, Blood Sugar, B12 and Iron levels.";
            return $"{mild} — Dizziness or weakness for {(int)days} day(s) is often due to Dehydration, low Blood Pressure, or low Blood Sugar.\n\n" +
                   "Drink ORS or coconut water right now. Have a light meal. Lie down and elevate your legs slightly.\n" +
                   "Check your blood pressure and blood sugar if possible.";
        }

        // Sore Throat
        if (s.Contains("sore throat"))
        {
            if (days >= 5)
                return $"{mod} — Sore throat lasting {(int)days} days may be Strep Throat (bacterial tonsillitis) and likely needs antibiotics.\n\n" +
                       "Please see a doctor for a throat swab test. Do NOT self-medicate with antibiotics.";
            return $"{mild} — Sore throat for {(int)days} day(s) is likely a Viral Throat Infection or early tonsillitis.\n\n" +
                   "Gargle warm salt water 3–4 times daily. Drink warm turmeric milk at night. " +
                   "Use throat lozenges and avoid cold drinks.\n" +
                   "See a doctor if pain is severe or you cannot swallow.";
        }

        // Rash / Itching
        if (s.Contains("rash") || s.Contains("itching") || s.Contains("hives"))
        {
            if (days >= 3)
                return $"{mod} — Rash or itching lasting {(int)days} days may be Contact Dermatitis, Eczema, or a drug reaction.\n\n" +
                       "Visit a dermatologist. Do not scratch — apply a plain moisturizer or mild hydrocortisone cream.";
            return $"{mild} — Rash or itching is likely an Allergic Reaction to food, soap, fabric, or plants.\n\n" +
                   "Apply a cold compress for relief. Take an antihistamine (e.g., Cetirizine 10mg, once daily).\n" +
                   "Identify and remove the trigger. See a doctor if the rash spreads rapidly.";
        }

        // Headache (no fever)
        if (s.Contains("headache"))
        {
            if (days >= 3)
                return $"{mod} — Headache lasting {(int)days} days may indicate Migraine, Sinusitis, or Hypertension.\n\n" +
                       "Please see a doctor. Check your blood pressure. Stay away from screens and bright light.";
            return $"{mild} — Headache for {(int)days} day(s) is likely a Tension Headache or early Migraine.\n\n" +
                   "Rest in a dark quiet room. Apply a cold pack to your forehead. " +
                   "Drink 2–3 glasses of water right now — dehydration is a very common cause.\n" +
                   "Take Paracetamol 500mg if needed.";
        }

        // Default fallback
        if (days >= 7)
            return $"{high} — You have had {s} for {(int)days} days. Symptoms lasting this long always need a doctor's evaluation.\n\n" +
                   "Please do not delay your visit any further.";

        return $"{mild} — You mentioned {s} for {(int)days} day(s). This may be a common infection or minor illness.\n\n" +
               "Rest well, stay hydrated, and eat light nutritious food. " +
               "See a doctor if symptoms worsen or do not improve within 2–3 days.";
    }

    // =============================================================
    //  MEDICAL REPORT — condition-specific remedies and tests
    // =============================================================
    private string GetMedicalReport(string symptoms, double days)
    {
        string urgency = GetUrgencyLevel(days);
        string durationText = days > 0 ? $"{(int)days} day(s)" : "Not specified";
        string condition = DetectConditionName(symptoms);
        string remedies = GetHomeRemedies(symptoms);
        string tests = GetSuggestedTests(symptoms, days);

        return $"📋 Medical Report Summary\n" +
               $"──────────────────────────\n" +
               $"Symptoms   : {symptoms}\n" +
               $"Duration   : {durationText}\n" +
               $"Likely     : {condition}\n" +
               $"Urgency    : {urgency}\n\n" +
               $"🏠 Home Remedies and Care\n" +
               $"{remedies}\n\n" +
               $"🔬 Suggested Tests\n" +
               $"{tests}\n\n" +
               $"⚕️ This is an AI-generated summary only.\n" +
               $"Please consult a qualified doctor for confirmation and treatment.";
    }

    // Identify most likely condition name from symptoms
    private string DetectConditionName(string s)
    {
        if (s.Contains("chest pain") || s.Contains("breathlessness"))
            return "Possible Cardiac / Respiratory Emergency";
        if (s.Contains("fever") && s.Contains("rash"))
            return "Possible Dengue Fever / Viral Exanthem";
        if (s.Contains("fever") && (s.Contains("headache") || s.Contains("body ache")))
            return "Viral Fever / Influenza / Possible Typhoid";
        if (s.Contains("cough") && s.Contains("chest pain"))
            return "Possible Bronchitis or Pneumonia";
        if (s.Contains("cough") && (s.Contains("cold") || s.Contains("runny nose")))
            return "Common Cold / Upper Respiratory Infection";
        if ((s.Contains("nausea") || s.Contains("vomiting")) && s.Contains("stomach pain"))
            return "Gastroenteritis / Food Poisoning";
        if (s.Contains("diarrhea"))
            return "Gastroenteritis / Intestinal Infection";
        if (s.Contains("dizziness") || s.Contains("fatigue") || s.Contains("weakness"))
            return "Dehydration / Anemia / Low Blood Pressure";
        if (s.Contains("sore throat"))
            return "Viral / Bacterial Throat Infection (Pharyngitis)";
        if (s.Contains("rash") || s.Contains("itching"))
            return "Allergic Reaction / Contact Dermatitis";
        if (s.Contains("headache"))
            return "Tension Headache / Migraine / Sinusitis";
        return "General Infection or Minor Illness";
    }

    // Condition-specific home remedies with local / Indian treatments
    private string GetHomeRemedies(string s)
    {
        if (s.Contains("chest pain") || s.Contains("breathlessness"))
            return "• No home remedy — seek emergency medical care immediately.";

        if (s.Contains("fever") && s.Contains("rash"))
            return "• Paracetamol only for fever — do NOT take Aspirin or Ibuprofen (increases bleeding risk in Dengue)\n" +
                   "• Drink 3+ liters of ORS or coconut water daily — fever + dengue cause rapid fluid loss\n" +
                   "• Papaya leaf extract juice — traditional remedy that may help support platelet count; ask your doctor first\n" +
                   "• Complete bed rest — avoid any physical exertion\n" +
                   "• Apply calamine lotion on the rash for itching and cooling relief\n" +
                   "• Wear loose, cotton clothing to reduce skin irritation";

        if (s.Contains("fever") && (s.Contains("headache") || s.Contains("body ache")))
            return "• Paracetamol 500mg every 6 hours (max 4 tablets/day) — safest way to bring fever down\n" +
                   "• Cool damp cloth on forehead and both armpits — very effective at reducing fever quickly\n" +
                   "• ORS or warm lemon water every 1–2 hours — fever causes rapid dehydration\n" +
                   "• Tulsi + ginger + black pepper tea (boil together, strain, drink 2–3 cups/day) — classic anti-fever remedy\n" +
                   "• Eat light food only: khichdi, dal-chawal, curd rice — easy on a feverish stomach\n" +
                   "• Cool sponge bath if fever exceeds 102°F — do not use ice-cold water\n" +
                   "• Avoid cold drinks, ice cream, oily and fried food completely while febrile";

        if (s.Contains("cough") && s.Contains("chest pain"))
            return "• Steam inhalation 2–3 times daily with a few drops of eucalyptus oil — clears mucus from airways\n" +
                   "• Honey + ginger juice (1 tsp each) — soothes airway inflammation and reduces cough reflex\n" +
                   "• Sleep with head elevated on 2 pillows — prevents mucus from pooling in the chest\n" +
                   "• Warm water and herbal teas throughout the day — keeps the airway moist and thin\n" +
                   "• Avoid cold drinks, dairy, smoking, and AC environments entirely\n" +
                   "• Do NOT suppress cough completely with cough syrup — coughing clears the infected airway";

        if (s.Contains("cough") && (s.Contains("cold") || s.Contains("runny nose") || s.Contains("sneezing")))
            return "• Steam inhalation with eucalyptus oil 2–3 times daily — best for nasal and chest congestion\n" +
                   "• Warm saline nasal rinse — dissolve 1/4 tsp salt in 1 cup warm water; rinse each nostril 2x daily\n" +
                   "• Ginger + honey + lemon tea — boil 4–5 ginger slices in 2 cups water, add 1 tsp honey + lemon; drink 3x daily\n" +
                   "• Tulsi (holy basil) kadha — boil 10 tulsi leaves, 2 cloves, 1/2 tsp ginger, pinch of black pepper; a highly effective traditional cold remedy\n" +
                   "• Warm saline gargle to soothe throat and reduce postnasal drip\n" +
                   "• Haldi milk (1/2 tsp turmeric in warm milk) at bedtime — strong anti-inflammatory and antimicrobial\n" +
                   "• Keep warm; avoid cold food, cold water, and cold or dusty air";

        if ((s.Contains("nausea") || s.Contains("vomiting")) && (s.Contains("stomach pain") || s.Contains("abdominal")))
            return "• Sip ORS slowly — 1 small sip every 5 minutes; do not gulp or you will vomit again\n" +
                   "• Homemade ORS if packets unavailable: 1 liter boiled water + 6 tsp sugar + 1/2 tsp salt\n" +
                   "• Fresh coconut water — excellent natural electrolyte replacement\n" +
                   "• BRAT diet once vomiting slows: Banana, plain boiled Rice, boiled Apple, dry Toast\n" +
                   "• Ginger tea or fresh ginger candy — naturally suppresses nausea without medication\n" +
                   "• Jeera (cumin) water — boil 1 tsp cumin in 2 cups water for 10 min; good for nausea and stomach cramps\n" +
                   "• Avoid dairy, raw vegetables, spicy and oily food for minimum 48 hours\n" +
                   "• Rest lying on your left side — helps digestion and reduces nausea";

        if (s.Contains("diarrhea"))
            return "• ORS is the most important treatment — drink 1 full glass after every loose motion to replace lost fluids\n" +
                   "• Homemade ORS: 1 liter water + 6 tsp sugar + 1/2 tsp salt; stir and sip throughout the day\n" +
                   "• Banana — firms loose stool naturally and replenishes lost potassium very effectively\n" +
                   "• Plain boiled rice with a pinch of salt and a little ghee — easiest food on an inflamed gut\n" +
                   "• Fresh curd (yogurt) with rice — restores beneficial gut bacteria disrupted by infection\n" +
                   "• Pomegranate juice — traditional Indian remedy shown to reduce frequency of loose motions\n" +
                   "• Avoid milk, raw fruits, leafy vegetables, spicy and caffeinated drinks until fully recovered\n" +
                   "• Wash hands with soap before eating and after every toilet visit — critical to prevent spread";

        if (s.Contains("dizziness") || s.Contains("fatigue") || s.Contains("weakness"))
            return "• Drink ORS or coconut water immediately if dizzy — dehydration is the most common cause\n" +
                   "• Eat something light right away if you feel very weak — low blood sugar is a very frequent cause\n" +
                   "• Lie down flat and elevate legs slightly — this pushes blood back toward the brain and helps immediately\n" +
                   "• For anemia: eat iron-rich foods daily — palak (spinach), jaggery (gud), dates, pomegranate, beetroot\n" +
                   "• Pair iron-rich food with Vitamin C (amla, lemon, orange) — it doubles iron absorption from food\n" +
                   "• Rise slowly from sitting or lying down — sudden standing causes dizziness from low blood pressure\n" +
                   "• Get 7–8 hours of quality sleep — persistent fatigue without sleep becomes a worsening cycle\n" +
                   "• Avoid tea and coffee on an empty stomach — they worsen dehydration and aggravate low BP";

        if (s.Contains("sore throat"))
            return "• Warm salt water gargle — 1/2 tsp salt in 1 glass warm water; gargle for 30 seconds, 4 times daily\n" +
                   "• Turmeric milk (haldi doodh) at bedtime — 1/2 tsp turmeric in warm milk; natural antibiotic and very soothing\n" +
                   "• Honey + raw ginger (1 tsp each, no water, taken directly) — coats the throat and actively fights infection; take 3x daily\n" +
                   "• Mulethi (licorice root) tea — very soothing traditional remedy; boil a small piece in water or chew raw\n" +
                   "• Throat lozenges (benzocaine or menthol-based) — temporary numbing and cooling relief between gargles\n" +
                   "• Wear a light scarf when going outside — keep the throat warm\n" +
                   "• Avoid cold drinks, ice cream, cold air, and dusty environments completely\n" +
                   "• Rest your voice — speak as little as possible if the throat is very sore";

        if (s.Contains("rash") || s.Contains("itching") || s.Contains("hives"))
            return "• Cold damp cloth on the rash — immediately reduces inflammation, redness, and itch\n" +
                   "• Fresh aloe vera gel applied directly to skin — very soothing, cooling, and anti-inflammatory\n" +
                   "• Pure coconut oil — gentle moisturizer with mild natural anti-inflammatory properties\n" +
                   "• Neem paste (grind fresh neem leaves to a paste) — powerful traditional antibacterial and anti-itch remedy\n" +
                   "• Oatmeal bath — grind 1 cup oatmeal, add to lukewarm bathwater; soothes widespread itching beautifully\n" +
                   "• Cetirizine 10mg once daily (available OTC as antihistamine) — significantly reduces allergic itching\n" +
                   "• Wear only loose, soft cotton clothing — avoid synthetic or tight fabrics at all times\n" +
                   "• Do NOT scratch — scratching breaks the skin surface and risks a bacterial infection on top\n" +
                   "• Identify and remove the trigger: common culprits are new soaps, detergents, foods, or plants";

        if (s.Contains("headache"))
            return "• Drink 2–3 glasses of water immediately — dehydration is the single most common cause of everyday headaches\n" +
                   "• Rest in a dark, quiet room with eyes fully closed for 20–30 minutes\n" +
                   "• Cold pack or ice in a cloth on forehead or neck — works best for migraine-type throbbing pain\n" +
                   "• Warm compress on neck and shoulders — works better for tension headache caused by muscle tightness\n" +
                   "• Peppermint oil — apply 2 drops to temples and rub in gently; a well-studied natural pain reliever\n" +
                   "• Clove oil or ground clove + salt paste applied to forehead — widely used effective Indian remedy\n" +
                   "• Ginger tea or cinnamon tea — anti-inflammatory; helps ease both tension and sinus headache\n" +
                   "• Paracetamol 500mg if pain is significant — do not use too frequently (max 3 times per week)\n" +
                   "• Avoid all screens, bright light, loud noise, and strong smells until the headache fully passes";

        return "• Rest at home and get 7–8 hours of sleep per night\n" +
               "• Drink 2–3 liters of water throughout the day\n" +
               "• Eat simple nutritious food — dal, rice, fruits, boiled vegetables\n" +
               "• Avoid alcohol, smoking, and junk food while unwell\n" +
               "• Monitor your temperature if fever develops\n" +
               "• See a doctor if symptoms worsen or last more than 3 days";
    }

    // Condition-specific suggested tests (not always the same generic panel)
    private string GetSuggestedTests(string s, double days)
    {
        if (s.Contains("chest pain") || s.Contains("breathlessness"))
            return "• ECG (electrocardiogram) — URGENT\n" +
                   "• Chest X-Ray\n" +
                   "• Troponin I/T and D-Dimer blood test\n" +
                   "• Pulse oximetry (blood oxygen level check)";

        if (s.Contains("fever") && s.Contains("rash"))
            return "• CBC with differential — platelet count is critical\n" +
                   "• Dengue NS1 Antigen test\n" +
                   "• Dengue IgM / IgG antibody test\n" +
                   "• Liver function test (LFT)\n" +
                   "• Chikungunya IgM (if joint pain is also present)";

        if (s.Contains("fever") && (s.Contains("headache") || s.Contains("body ache")))
        {
            if (days >= 5)
                return "• CBC with differential\n" +
                       "• Widal test — for Typhoid, relevant only after day 5 of fever\n" +
                       "• Dengue NS1 Antigen + IgM/IgG\n" +
                       "• Malaria antigen test (Rapid Malaria Test or blood smear)\n" +
                       "• Blood culture — if fever has persisted beyond 7 days\n" +
                       "• CRP (C-Reactive Protein) — measures severity of infection";
            return "• CBC with differential\n" +
                   "• CRP (C-Reactive Protein)\n" +
                   "• Dengue NS1 Antigen (if fever has lasted beyond 3 days)\n" +
                   "• Blood sugar — fasting";
        }

        if (s.Contains("cough") && s.Contains("chest pain"))
            return "• Chest X-Ray — first priority\n" +
                   "• CBC with differential\n" +
                   "• CRP and ESR (inflammation markers)\n" +
                   "• Sputum culture and sensitivity (if cough is productive with phlegm)\n" +
                   "• Pulse oximetry";

        if (s.Contains("cough") || s.Contains("cold"))
        {
            if (days >= 7)
                return "• CBC with differential\n" +
                       "• CRP and ESR\n" +
                       "• Throat swab culture\n" +
                       "• Chest X-Ray if cough is deep or productive";
            return "• No tests are usually needed for a simple cold in the first 5 days\n" +
                   "• If symptoms worsen: CBC and CRP\n" +
                   "• Throat swab culture if bacterial tonsillitis is suspected";
        }

        if (s.Contains("nausea") || s.Contains("vomiting") || s.Contains("diarrhea"))
            return "• Stool routine and microscopy\n" +
                   "• Stool culture and sensitivity — if diarrhea persists beyond 3 days\n" +
                   "• CBC with differential\n" +
                   "• Serum electrolytes — sodium, potassium, chloride — if severe vomiting or diarrhea\n" +
                   "• Liver function test — if yellowing of skin or eyes is also noticed";

        if (s.Contains("dizziness") || s.Contains("fatigue") || s.Contains("weakness"))
            return "• CBC with peripheral blood smear — identify type of anemia\n" +
                   "• Serum iron, ferritin, Vitamin B12, folate levels\n" +
                   "• Thyroid profile — TSH, Free T3, Free T4 — rules out hypothyroidism\n" +
                   "• Blood sugar — fasting and post-prandial (2 hours after a meal)\n" +
                   "• Blood pressure measurement — check for low BP and postural hypotension";

        if (s.Contains("sore throat"))
        {
            if (days >= 5)
                return "• Throat swab culture — confirms Streptococcal (bacterial) infection\n" +
                       "• CBC with differential\n" +
                       "• CRP if bacterial infection is suspected";
            return "• Usually no tests needed in the first 4 days\n" +
                   "• Throat swab culture only if no improvement by day 5\n" +
                   "• CBC if fever is also present";
        }

        if (s.Contains("rash") || s.Contains("itching"))
            return "• CBC with eosinophil count — elevated in allergic conditions\n" +
                   "• Total IgE level — main allergy blood marker\n" +
                   "• Patch test — for contact dermatitis, performed by a dermatologist\n" +
                   "• Skin scraping with KOH test — if fungal infection is suspected";

        if (s.Contains("headache") && days >= 5)
            return "• Blood pressure measurement — rule out hypertension\n" +
                   "• CBC\n" +
                   "• Blood sugar fasting\n" +
                   "• CT scan or MRI brain — ONLY if headache is sudden and severe, or accompanied by vomiting, vision changes, or neck stiffness";

        return "• CBC (Complete Blood Count)\n" +
               "• Blood sugar — fasting\n" +
               "• Urine routine and microscopy\n" +
               "• CRP (inflammation marker)";
    }

    // =============================================================
    //  VISIT DOCTOR ADVICE — urgency-aware based on symptoms + days
    // =============================================================
    private string GetVisitDoctorAdvice(string s, double days)
    {
        if (s.Contains("chest pain") || s.Contains("breathlessness"))
            return "🚨 YES — Go to the emergency room RIGHT NOW. Chest pain and breathlessness are medical emergencies. Do not drive yourself.";

        if (s.Contains("fever") && s.Contains("rash"))
            return "✅ YES — Visit a doctor today. Fever with rash can indicate Dengue, which can become serious quickly. Early treatment matters.";

        if (days >= 7)
            return $"✅ YES — Symptoms lasting {(int)days} days need proper medical evaluation. Please visit a doctor today. Do not delay any further.";

        if (days >= 4)
            return $"⚠️ RECOMMENDED — Your symptoms have continued for {(int)days} days. Visit a clinic within 24 hours, " +
                   "especially if there is no improvement or things are getting worse.";

        return "🟢 NOT URGENT RIGHT NOW — Your symptoms appear mild and recent.\n\n" +
               "Continue home care for 2–3 more days. Visit a doctor if any of these happen:\n" +
               "• Fever goes above 103°F / 39.5°C\n" +
               "• Symptoms suddenly become much worse\n" +
               "• You feel very weak, confused, or cannot keep fluids down\n" +
               "• No improvement after 3 days of home care";
    }

    // Urgency label used in medical report header
    private string GetUrgencyLevel(double days)
    {
        if (days >= 7) return "🔴 HIGH — Symptoms for 7+ days. See a doctor TODAY.";
        if (days >= 4) return "🟡 MEDIUM — Symptoms for 4–6 days. Visit a clinic soon.";
        if (days >= 1) return "🟢 LOW — Symptoms for 1–3 days. Monitor at home.";
        return "🟢 LOW — Monitor at home and rest well.";
    }
}