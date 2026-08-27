using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using System.Net.Http;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AniDB.Configuration;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata
{
    public class AniDbSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
    {
        private const string SeriesDataFile = "series.xml";
        private const string SeriesQueryUrl = "http://api.anidb.net:9001/httpapi?request=anime&client={0}&clientver=1&protover=1&aid={1}";
        private const string ClientName = "mediabrowser";

        // AniDB has very low request rate limits, a minimum of 2 seconds between requests, and an average of 4 seconds between requests
        public static readonly RateLimiter RequestLimiter = new RateLimiter(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
        private static readonly int[] IgnoredTagIds = { 6, 22, 23, 60, 128, 129, 185, 216, 242, 255, 268, 269, 289 };
        private static readonly Regex AniDbUrlRegex = new Regex(@"https?://anidb.net/\w+(/[0-9]+)? \[(?<name>[^\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex _errorRegex = new(@"<error code=""[0-9]+"">[a-zA-Z]+</error>", RegexOptions.Compiled);
        private readonly IApplicationPaths _appPaths;

        private readonly Dictionary<string, PersonKind> _typeMappings = new()
        {
            // MANUAL
            {"Direction", PersonKind.Director},
            {"Music", PersonKind.Composer},
            {"Work", PersonKind.Producer},
            {"Production", PersonKind.Producer},
            {"Chief Animation Direction", PersonKind.Director},
            {"Chief Direction", PersonKind.Director},
            {"Original Work", PersonKind.Creator},
            {"Original Character Design", PersonKind.Creator},
            {"Series Composition", PersonKind.Writer},
            {"Character Design", PersonKind.Illustrator},

            // AI added
            {"1st Key Animation", PersonKind.Artist},
            {"2D Animation", PersonKind.Artist},
            {"2D Art Design", PersonKind.Illustrator},
            {"2D Art Direction", PersonKind.Director},
            {"2D Design", PersonKind.Illustrator},
            {"2D Design Assistance", PersonKind.Illustrator},
            {"2D Digital", PersonKind.Artist},
            {"2D Digital Work Assistance", PersonKind.Artist},
            {"2D Digital Works", PersonKind.Artist},
            {"2D Director", PersonKind.Director},
            {"2D Editing", PersonKind.Editor},
            {"2D Effects", PersonKind.Artist},
            {"2D Effects Chief", PersonKind.Artist},
            {"2D Effects Director", PersonKind.Director},
            {"2D Graphics", PersonKind.Artist},
            {"2D Management", PersonKind.Producer},
            {"2D Material Design", PersonKind.Illustrator},
            {"2D Modelling", PersonKind.Artist},
            {"2D Modelling Lead", PersonKind.Artist},
            {"2D Modelling Support", PersonKind.Artist},
            {"2D Motion", PersonKind.Artist},
            {"2D Supervisor", PersonKind.Director},
            {"2D Works", PersonKind.Artist},
            {"2D Works Assistance", PersonKind.Artist},
            {"2D Works Design", PersonKind.Illustrator},
            {"2D Works Digital Correction", PersonKind.Artist},
            {"2D-3D Conversion", PersonKind.Artist},
            {"2DCG", PersonKind.Artist},
            {"2DCG Work", PersonKind.Artist},
            {"2nd Key Animation", PersonKind.Artist},
            {"2nd Key Animation Assistance", PersonKind.Artist},
            {"3D Action Adviser", PersonKind.Artist},
            {"3D Animation Direction", PersonKind.Director},
            {"3D Art Chief", PersonKind.Artist},
            {"3D Art Design", PersonKind.Illustrator},
            {"3D Art Work", PersonKind.Artist},
            {"3D Background Art", PersonKind.Artist},
            {"3D Background Art Assistance", PersonKind.Artist},
            {"3D Background Art Manager", PersonKind.Producer},
            {"3D Background Art Matte Painting Supervisor", PersonKind.Director},
            {"3D Background Art Processing/Scanning", PersonKind.Artist},
            {"3D Background Chief", PersonKind.Artist},
            {"3D Background Modelling", PersonKind.Artist},
            {"3D Background Modelling Assistance", PersonKind.Artist},
            {"3D Composition Work", PersonKind.Artist},
            {"3D Effects", PersonKind.Artist},
            {"3D Effects Setup", PersonKind.Artist},
            {"3D Layout", PersonKind.Artist},
            {"3D Layout Supervision", PersonKind.Director},
            {"3D Lead Director", PersonKind.Director},
            {"3D Lighting", PersonKind.Artist},
            {"3D Lighting Assistance", PersonKind.Artist},
            {"3D Matte Painter", PersonKind.Artist},
            {"3D Mechanical Design", PersonKind.Illustrator},
            {"3D Motion Work", PersonKind.Artist},
            {"3D Motion Work Assistance", PersonKind.Artist},
            {"3D Producer", PersonKind.Producer},
            {"3D Remodelling", PersonKind.Artist},
            {"3D Sound", PersonKind.Engineer},
            {"3D Stage Design", PersonKind.Illustrator},
            {"3D Works", PersonKind.Artist},
            {"3DCG", PersonKind.Artist},
            {"3DCG & Compositing", PersonKind.Artist},
            {"3DCG Action", PersonKind.Artist},
            {"3DCG Advisor", PersonKind.Artist},
            {"3DCG Animation", PersonKind.Artist},
            {"3DCG Animation Assistance", PersonKind.Artist},
            {"3DCG Animation Chief", PersonKind.Artist},
            {"3DCG Assistance", PersonKind.Artist},
            {"3DCG Assistant Manager", PersonKind.Producer},
            {"3DCG Assistant Production Manager", PersonKind.Producer},
            {"3DCG Guide", PersonKind.Artist},
            {"3DCG Guide Animator", PersonKind.Artist},
            {"3DCG Guide Production Manager", PersonKind.Producer},
            {"3DCG Map Creation", PersonKind.Artist},
            {"3DCG Photography", PersonKind.Artist},
            {"3DCG Visual Direction", PersonKind.Director},
            {"A&R Producer", PersonKind.Producer},
            {"ADR & Dubbing Stage", PersonKind.Artist},
            {"ADR Editor", PersonKind.Editor},
            {"ADR Engineer", PersonKind.Engineer},
            {"ADR Mixer", PersonKind.Mixer},
            {"ADR Recording Engineer", PersonKind.Engineer},
            {"ADR Stage", PersonKind.Artist},
            {"ADR Studio", PersonKind.Artist},
            {"AVID Editing", PersonKind.Editor},
            {"Accessory Animation Direction", PersonKind.Director},
            {"Accessory Assistance", PersonKind.Artist},
            {"Accessory Design", PersonKind.Illustrator},
            {"Accessory Design Assistance", PersonKind.Illustrator},
            {"Account Executive", PersonKind.Artist},
            {"Accounting", PersonKind.Artist},
            {"Accounting Manager", PersonKind.Producer},
            {"Acoustic Guitar", PersonKind.Artist},
            {"Acting Assistance", PersonKind.Artist},
            {"Acting Office Work", PersonKind.Artist},
            {"Action Animation Direction", PersonKind.Director},
            {"Action Animation Direction Assistance", PersonKind.Director},
            {"Action Animation Supervision", PersonKind.Director},
            {"Action Assistance", PersonKind.Artist},
            {"Action Design", PersonKind.Illustrator},
            {"Action Design Assistance", PersonKind.Illustrator},
            {"Action Direction", PersonKind.Director},
            {"Action Direction Assistance", PersonKind.Director},
            {"Action Scene Assistance", PersonKind.Artist},
            {"Action Storyboard", PersonKind.Director},
            {"Action Storyboard Assistance", PersonKind.Director},
            {"Action Supervision", PersonKind.Director},
            {"Actor", PersonKind.Actor},
            {"Adaptation", PersonKind.Artist},
            {"Additional Music", PersonKind.Composer},
            {"Additional Production Design", PersonKind.Illustrator},
            {"Additional Score", PersonKind.Composer},
            {"Administrative Assistant", PersonKind.Artist},
            {"Advert Design", PersonKind.Illustrator},
            {"Advertising", PersonKind.Artist},
            {"Advisor", PersonKind.Artist},
            {"After Effects", PersonKind.Artist},
            {"Airbrush Work", PersonKind.Artist},
            {"Animation Asset Design", PersonKind.Illustrator},
            {"Animation Assistant", PersonKind.Artist},
            {"Animation Character Design", PersonKind.Illustrator},
            {"Animation Chief", PersonKind.Artist},
            {"Animation Concept", PersonKind.Artist},
            {"Animation Coordinator", PersonKind.Producer},
            {"Animation Creature Design", PersonKind.Illustrator},
            {"Animation Design", PersonKind.Illustrator},
            {"Animation Direction", PersonKind.Director},
            {"Animation Direction Supervision", PersonKind.Director},
            {"Animation Guidance Advisor", PersonKind.Artist},
            {"Animation Line Producer", PersonKind.Producer},
            {"Animation Manager", PersonKind.Producer},
            {"Animation Materials", PersonKind.Artist},
            {"Animation Mechanical Design", PersonKind.Illustrator},
            {"Animation Mechanical Direction", PersonKind.Director},
            {"Animation Planning", PersonKind.Producer},
            {"Animation Producer", PersonKind.Producer},
            {"Animation Production Chief", PersonKind.Producer},
            {"Animation Prop Design", PersonKind.Illustrator},
            {"Animation Reference Models", PersonKind.Artist},
            {"Animation Sub Character Design", PersonKind.Illustrator},
            {"Animation Supervision", PersonKind.Director},
            {"Animation Tool", PersonKind.Artist},
            {"Animation Work", PersonKind.Artist},
            {"Animation Work Assistance", PersonKind.Artist},
            {"Animation Work Assistant", PersonKind.Artist},
            {"Animation Work Manager", PersonKind.Producer},
            {"Animator", PersonKind.Artist},
            {"Armour Modelling", PersonKind.Artist},
            {"Arrangement Assistant", PersonKind.Arranger},
            {"Art", PersonKind.Artist},
            {"Art Advisor", PersonKind.Artist},
            {"Art Animation Direction", PersonKind.Director},
            {"Art Assistant", PersonKind.Artist},
            {"Art Board", PersonKind.Artist},
            {"Art Board Assistance", PersonKind.Artist},
            {"Art Board Work Assistance", PersonKind.Artist},
            {"Art Chief", PersonKind.Artist},
            {"Art Coordination", PersonKind.Artist},
            {"Art Design", PersonKind.Illustrator},
            {"Art Design 2D Modelling Support", PersonKind.Illustrator},
            {"Art Design Assistance", PersonKind.Illustrator},
            {"Art Design Supervision", PersonKind.Director},
            {"Art Direction", PersonKind.Director},
            {"Art Direction Support", PersonKind.Director},
            {"Art Layout", PersonKind.Artist},
            {"Art Layout Assistance", PersonKind.Artist},
            {"Art Manager", PersonKind.Producer},
            {"Art Manager Assistance", PersonKind.Producer},
            {"Art Producer", PersonKind.Producer},
            {"Art Production", PersonKind.Producer},
            {"Art Supervision", PersonKind.Director},
            {"Art Supervision Assistance", PersonKind.Director},
            {"Art Team", PersonKind.Artist},
            {"Art Work", PersonKind.Artist},
            {"Art Work Assistance", PersonKind.Artist},
            {"Art and Literature", PersonKind.Artist},
            {"Art and Literature Assistance", PersonKind.Artist},
            {"Art and Literature Generalisation", PersonKind.Artist},
            {"Art and Literature Supervision", PersonKind.Director},
            {"Artist", PersonKind.Artist},
            {"Artwork Coordinator", PersonKind.Producer},
            {"Artwork Design", PersonKind.Illustrator},
            {"Asset Coordinator", PersonKind.Producer},
            {"Asset Design", PersonKind.Illustrator},
            {"Assistance/Cooperation", PersonKind.Artist},
            {"Assistant Animation Direction", PersonKind.Director},
            {"Assistant Animation Producer", PersonKind.Producer},
            {"Assistant Animation Supervision", PersonKind.Director},
            {"Assistant Art Chief", PersonKind.Artist},
            {"Assistant Art Direction", PersonKind.Director},
            {"Assistant Art Manager", PersonKind.Producer},
            {"Assistant Background Art Manager", PersonKind.Producer},
            {"Assistant Chief Direction", PersonKind.Director},
            {"Assistant Chief Manager", PersonKind.Producer},
            {"Assistant Design Manager", PersonKind.Producer},
            {"Assistant Dialogue Director", PersonKind.Director},
            {"Assistant Direction", PersonKind.Director},
            {"Assistant Director`s Assistant", PersonKind.Director},
            {"Assistant Episode Direction", PersonKind.Director},
            {"Assistant General Producer", PersonKind.Producer},
            {"Assistant Layout Animation Direction", PersonKind.Director},
            {"Assistant Line Producer", PersonKind.Producer},
            {"Assistant Manager", PersonKind.Producer},
            {"Assistant Music Director", PersonKind.Director},
            {"Assistant Music Producer", PersonKind.Producer},
            {"Assistant Producer", PersonKind.Producer},
            {"Assistant Producer`s Assistant", PersonKind.Producer},
            {"Assistant Production Manager", PersonKind.Producer},
            {"Assistant Production Manager Assistance", PersonKind.Producer},
            {"Assistant Publicity Manager", PersonKind.Producer},
            {"Assistant Sound Editor", PersonKind.Engineer},
            {"Assistant Sound Work Manager", PersonKind.Producer},
            {"Assistant Supervising Sound Editor", PersonKind.Engineer},
            {"Assisting Production", PersonKind.Producer},
            {"Associate", PersonKind.Artist},
            {"Associate Animation Producer", PersonKind.Producer},
            {"Associate Character Design", PersonKind.Illustrator},
            {"Associate Director", PersonKind.Director},
            {"Associate Executive Producer", PersonKind.Producer},
            {"Associate Manager", PersonKind.Producer},
            {"Associate Music Producer", PersonKind.Producer},
            {"Associate Producer", PersonKind.Producer},
            {"Associate Producer Assistant", PersonKind.Producer},
            {"Associated Product Production", PersonKind.Producer},
            {"Audio Director", PersonKind.Director},
            {"Auditing", PersonKind.Artist},
            {"Audition Assistance", PersonKind.Artist},
            {"Authoring", PersonKind.Author},
            {"Avant Animation", PersonKind.Artist},
            {"Back Sponsor Screen Illustration", PersonKind.Illustrator},
            {"Back Sponsor Screen Illustration Assistance", PersonKind.Illustrator},
            {"Background Art", PersonKind.Artist},
            {"Background Art Assistance", PersonKind.Artist},
            {"Background Art Assistance Studio", PersonKind.Artist},
            {"Background Art Chief", PersonKind.Artist},
            {"Background Art Collage", PersonKind.Artist},
            {"Background Art Correction", PersonKind.Artist},
            {"Background Art Design", PersonKind.Illustrator},
            {"Background Art Design Assistance", PersonKind.Illustrator},
            {"Background Art Digital Correction", PersonKind.Artist},
            {"Background Art Director", PersonKind.Director},
            {"Background Art Effects", PersonKind.Artist},
            {"Background Art Generalisation", PersonKind.Artist},
            {"Background Art Image Board", PersonKind.Artist},
            {"Background Art Layout", PersonKind.Artist},
            {"Background Art Layout Assistance", PersonKind.Artist},
            {"Background Art Layout Scan", PersonKind.Artist},
            {"Background Art Manager", PersonKind.Producer},
            {"Background Art Mask", PersonKind.Artist},
            {"Background Art Materials Assistance", PersonKind.Artist},
            {"Background Art Photography", PersonKind.Artist},
            {"Background Art Processing/Scanning", PersonKind.Artist},
            {"Background Art Processing/Scanning Adjustment", PersonKind.Artist},
            {"Background Art Producer", PersonKind.Producer},
            {"Background Art Production Manager", PersonKind.Producer},
            {"Background Art Retouch", PersonKind.Artist},
            {"Background Art Retouch Assistance", PersonKind.Artist},
            {"Background Art Studio", PersonKind.Artist},
            {"Background Art Styling", PersonKind.Artist},
            {"Background Art Supervision", PersonKind.Director},
            {"Background Art Technical Director", PersonKind.Director},
            {"Background Art Tracing", PersonKind.Artist},
            {"Background Art Work Assistance", PersonKind.Artist},
            {"Background Concept Artist", PersonKind.Artist},
            {"Background Design Assistance", PersonKind.Illustrator},
            {"Background Layout", PersonKind.Artist},
            {"Background Layout Adjustment", PersonKind.Artist},
            {"Background Layout Scanning Assistance", PersonKind.Artist},
            {"Background Line Drawings", PersonKind.Artist},
            {"Background Modelling", PersonKind.Artist},
            {"Background Modelling Lead", PersonKind.Artist},
            {"Background Modelling Supervision", PersonKind.Director},
            {"Background Music Recording", PersonKind.Composer},
            {"Background Music Recording Assistance", PersonKind.Composer},
            {"Background Music Staff", PersonKind.Composer},
            {"Background Music Work", PersonKind.Composer},
            {"Background Music Work Assistance", PersonKind.Composer},
            {"Background Music Work Assistant Director", PersonKind.Director},
            {"Background Music Work Director", PersonKind.Director},
            {"Background Music Work Producer", PersonKind.Producer},
            {"Background Support", PersonKind.Artist},
            {"Background Technical Line Advisor", PersonKind.Artist},
            {"Banjo", PersonKind.Artist},
            {"Base Planning", PersonKind.Producer},
            {"Base Planning Assistance", PersonKind.Producer},
            {"Bass", PersonKind.Artist},
            {"Bibliography", PersonKind.Artist},
            {"Bibliography Assistance", PersonKind.Artist},
            {"Blu-ray & DVD Work", PersonKind.Artist},
            {"Blu-ray Work", PersonKind.Artist},
            {"Bonus Manga Animation", PersonKind.Artist},
            {"Bonus Manga Work", PersonKind.Artist},
            {"Bonus Video Animation", PersonKind.Artist},
            {"Bonus Video Content", PersonKind.Artist},
            {"Bonus Video Work", PersonKind.Artist},
            {"Brand Manager", PersonKind.Producer},
            {"Brass", PersonKind.Artist},
            {"Broadband Distribution", PersonKind.Artist},
            {"Broadcast Assistance", PersonKind.Artist},
            {"Brush Art", PersonKind.Artist},
            {"Brush Art Assistance", PersonKind.Artist},
            {"Brush Art Materials", PersonKind.Artist},
            {"Brush Work", PersonKind.Artist},
            {"Business Coordination", PersonKind.Artist},
            {"Business Producer", PersonKind.Producer},
            {"CG Action Director", PersonKind.Director},
            {"CG Action Supervision", PersonKind.Director},
            {"CG Additional Production Design", PersonKind.Illustrator},
            {"CG Additional Production Design Assistance", PersonKind.Illustrator},
            {"CG Animation Direction", PersonKind.Director},
            {"CG Animation Producer", PersonKind.Producer},
            {"CG Animation Work", PersonKind.Artist},
            {"CG Art", PersonKind.Artist},
            {"CG Art Assistance", PersonKind.Artist},
            {"CG Art Design", PersonKind.Illustrator},
            {"CG Art Direction", PersonKind.Director},
            {"CG Artist", PersonKind.Artist},
            {"CG Assets", PersonKind.Artist},
            {"CG Assistance", PersonKind.Artist},
            {"CG Assistant Producer", PersonKind.Producer},
            {"CG Background Support", PersonKind.Artist},
            {"CG Character Design", PersonKind.Illustrator},
            {"CG Chief", PersonKind.Artist},
            {"CG Chief Producer", PersonKind.Producer},
            {"CG Composite Direction", PersonKind.Director},
            {"CG Compositing", PersonKind.Artist},
            {"CG Compositing Effects", PersonKind.Artist},
            {"CG Composition Assistant", PersonKind.Artist},
            {"CG Coordinator", PersonKind.Producer},
            {"CG Coordinator Assistance", PersonKind.Producer},
            {"CG Creative Director", PersonKind.Director},
            {"CG Crew", PersonKind.Artist},
            {"CG Design", PersonKind.Illustrator},
            {"CG Design Assistance", PersonKind.Illustrator},
            {"CG Development", PersonKind.Artist},
            {"CG Direction", PersonKind.Director},
            {"CG Direction Assistance", PersonKind.Director},
            {"CG Editing", PersonKind.Editor},
            {"CG Effect Coordinator", PersonKind.Producer},
            {"CG Effects Direction", PersonKind.Director},
            {"CG Engineer", PersonKind.Engineer},
            {"CG Key Animation", PersonKind.Artist},
            {"CG Layout", PersonKind.Artist},
            {"CG Layout Assistance", PersonKind.Artist},
            {"CG Layout Design", PersonKind.Illustrator},
            {"CG Layout Guide", PersonKind.Artist},
            {"CG Layout Manager", PersonKind.Producer},
            {"CG Layout Modelling", PersonKind.Artist},
            {"CG Layout Modelling Assistance", PersonKind.Artist},
            {"CG Layout Production Manager", PersonKind.Producer},
            {"CG Layout Supervision", PersonKind.Director},
            {"CG Line Coordinator", PersonKind.Producer},
            {"CG Line Director", PersonKind.Director},
            {"CG Line Manager", PersonKind.Producer},
            {"CG Line Producer", PersonKind.Producer},
            {"CG Manager", PersonKind.Producer},
            {"CG Mechanical Direction", PersonKind.Director},
            {"CG Motion", PersonKind.Artist},
            {"CG Motion Photography", PersonKind.Artist},
            {"CG Office Work", PersonKind.Artist},
            {"CG Operator", PersonKind.Artist},
            {"CG Planning", PersonKind.Producer},
            {"CG Processing", PersonKind.Artist},
            {"CG Producer", PersonKind.Producer},
            {"CG Production Assistance", PersonKind.Producer},
            {"CG Production Associate", PersonKind.Producer},
            {"CG Production Design", PersonKind.Illustrator},
            {"CG Production Desk", PersonKind.Producer},
            {"CG Production Generalisation", PersonKind.Producer},
            {"CG Production Generalisation Assistance", PersonKind.Producer},
            {"CG Production Manager", PersonKind.Producer},
            {"CG Production Support", PersonKind.Producer},
            {"CG Prop Design", PersonKind.Illustrator},
            {"CG Software", PersonKind.Artist},
            {"CG Special Effects Animation", PersonKind.Artist},
            {"CG Staff Chief", PersonKind.Artist},
            {"CG Staff Roll", PersonKind.Artist},
            {"CG Studio", PersonKind.Artist},
            {"CG Sub Director", PersonKind.Director},
            {"CG Sub Line Director", PersonKind.Director},
            {"CG Sub Work", PersonKind.Artist},
            {"CG Supervision", PersonKind.Director},
            {"CG Support", PersonKind.Artist},
            {"CG Technical Advisor", PersonKind.Artist},
            {"CG Technical Director", PersonKind.Director},
            {"CG Technical Programmer", PersonKind.Artist},
            {"CG Technical Supervisor", PersonKind.Director},
            {"CG Technical Support", PersonKind.Artist},
            {"CG Visual Design", PersonKind.Illustrator},
            {"CG Work", PersonKind.Artist},
            {"CG Work Assistance", PersonKind.Artist},
            {"CG Work Coordinator", PersonKind.Producer},
            {"CG Work Studio", PersonKind.Artist},
            {"CGI", PersonKind.Artist},
            {"CM Work", PersonKind.Artist},
            {"CM Work Assistance", PersonKind.Artist},
            {"Calibration", PersonKind.Artist},
            {"Calibration Effects", PersonKind.Artist},
            {"Calligraphy", PersonKind.Artist},
            {"Calligraphy Design", PersonKind.Illustrator},
            {"Camera Assistant", PersonKind.Artist},
            {"Cameraman", PersonKind.Artist},
            {"Capture Photography Assistant", PersonKind.Artist},
            {"Cardfight Composition", PersonKind.Artist},
            {"Casting", PersonKind.Artist},
            {"Casting Assistance", PersonKind.Artist},
            {"Casting Coordination", PersonKind.Artist},
            {"Casting Direction", PersonKind.Director},
            {"Casting Management", PersonKind.Producer},
            {"Casting Management Chief", PersonKind.Producer},
            {"Casting Office Work", PersonKind.Artist},
            {"Casting Producer", PersonKind.Producer},
            {"Cel Animation Remixer", PersonKind.Remixer},
            {"Cel Inspection", PersonKind.Artist},
            {"Cel Inspection Assistance", PersonKind.Artist},
            {"Cel Special Effects", PersonKind.Artist},
            {"Cel Work", PersonKind.Artist},
            {"Cello", PersonKind.Artist},
            {"Cellophane Shadow Puppet Animation", PersonKind.Artist},
            {"Chairman", PersonKind.Artist},
            {"Character Acting Direction", PersonKind.Director},
            {"Character Amendment", PersonKind.Artist},
            {"Character Animation Direction", PersonKind.Director},
            {"Character Animation Direction Assistance", PersonKind.Director},
            {"Character Animation Supervision", PersonKind.Director},
            {"Character Card", PersonKind.Artist},
            {"Character Colour Design", PersonKind.Colorist},
            {"Character Concept Assistance", PersonKind.Artist},
            {"Character Concept Design", PersonKind.Illustrator},
            {"Character Design Assistance", PersonKind.Illustrator},
            {"Character Design Supervision", PersonKind.Director},
            {"Character Design Works", PersonKind.Illustrator},
            {"Character Key Animation", PersonKind.Artist},
            {"Character Mechanic", PersonKind.Artist},
            {"Character Merchandise Development", PersonKind.Artist},
            {"Character Modelling", PersonKind.Artist},
            {"Character Modelling Assistance", PersonKind.Artist},
            {"Character Modelling Director", PersonKind.Director},
            {"Character Modelling Lead", PersonKind.Artist},
            {"Character Modelling Supervision", PersonKind.Director},
            {"Character Retouch", PersonKind.Artist},
            {"Character Set-up", PersonKind.Artist},
            {"Character Supervision", PersonKind.Director},
            {"Chief 3D Layout", PersonKind.Artist},
            {"Chief Accessory Animation Direction", PersonKind.Director},
            {"Chief Action Director", PersonKind.Director},
            {"Chief Animation Direction Assistance", PersonKind.Director},
            {"Chief Animation Producer", PersonKind.Producer},
            {"Chief Animation Supervision", PersonKind.Director},
            {"Chief Animator", PersonKind.Artist},
            {"Chief Art Direction", PersonKind.Director},
            {"Chief Assistant Production Manager", PersonKind.Producer},
            {"Chief CG Animator", PersonKind.Artist},
            {"Chief CG Design", PersonKind.Illustrator},
            {"Chief CG Direction", PersonKind.Director},
            {"Chief Character Animation Direction", PersonKind.Director},
            {"Chief Character Animation Direction Assistance", PersonKind.Director},
            {"Chief Character Design", PersonKind.Illustrator},
            {"Chief Colouring", PersonKind.Colorist},
            {"Chief Creature Animation Direction", PersonKind.Director},
            {"Chief Designer", PersonKind.Illustrator},
            {"Chief Effects Animation Direction", PersonKind.Director},
            {"Chief Engineer", PersonKind.Engineer},
            {"Chief Episode Direction", PersonKind.Director},
            {"Chief Financial Officer", PersonKind.Artist},
            {"Chief In-Between Animation Supervision", PersonKind.Director},
            {"Chief Layout Animation Direction", PersonKind.Director},
            {"Chief Manager", PersonKind.Producer},
            {"Chief Mechanical Animation Direction", PersonKind.Director},
            {"Chief Mechanical Animation Direction Assistance", PersonKind.Director},
            {"Chief Music Producer", PersonKind.Producer},
            {"Chief Operator", PersonKind.Artist},
            {"Chief Photographic Direction", PersonKind.Director},
            {"Chief Producer", PersonKind.Producer},
            {"Chief Production Desk", PersonKind.Producer},
            {"Chief Promotor", PersonKind.Artist},
            {"Chief Publicity Producer", PersonKind.Producer},
            {"Chief Researcher", PersonKind.Artist},
            {"Chief Sound Mixing", PersonKind.Mixer},
            {"Chief Supervision", PersonKind.Director},
            {"Chief Supervision Assistant", PersonKind.Director},
            {"Chief Weapon Animation Direction", PersonKind.Director},
            {"Chief Weapon Supervision", PersonKind.Director},
            {"Chinese Subtitles", PersonKind.Translator},
            {"Choir", PersonKind.Artist},
            {"Choir Assistance", PersonKind.Artist},
            {"Choir Conducting", PersonKind.Artist},
            {"Choreography", PersonKind.Artist},
            {"Choreography Assistance", PersonKind.Artist},
            {"Choreography Coordinator", PersonKind.Producer},
            {"Choreography Supervision", PersonKind.Director},
            {"Choreography Video Coordinator", PersonKind.Producer},
            {"Choreography Video Filming", PersonKind.Artist},
            {"Chorus Coordinator", PersonKind.Producer},
            {"Cine Book", PersonKind.Artist},
            {"Clay Animation", PersonKind.Artist},
            {"Clay Crew", PersonKind.Artist},
            {"Clay Model Work", PersonKind.Artist},
            {"Client Services", PersonKind.Artist},
            {"Clothing Assistance", PersonKind.Artist},
            {"Clothing Design", PersonKind.Illustrator},
            {"Clothing Design Assistance", PersonKind.Illustrator},
            {"Co-Executive Producer", PersonKind.Producer},
            {"Colour Advisor", PersonKind.Colorist},
            {"Colour Board", PersonKind.Colorist},
            {"Colour Coordination", PersonKind.Colorist},
            {"Colour Coordinator", PersonKind.Producer},
            {"Colour Correction", PersonKind.Colorist},
            {"Colour Design", PersonKind.Colorist},
            {"Colour Design Assistance", PersonKind.Colorist},
            {"Colour Design Supervision", PersonKind.Director},
            {"Colour Management", PersonKind.Producer},
            {"Colour Management System", PersonKind.Producer},
            {"Colour Specification", PersonKind.Colorist},
            {"Colour Specification Assistance", PersonKind.Colorist},
            {"Colour Specification Inspection Assistance", PersonKind.Colorist},
            {"Colour Specification Supervision", PersonKind.Director},
            {"Colour Specification/Cel Inspection", PersonKind.Colorist},
            {"Colour Supervision", PersonKind.Director},
            {"Colouring", PersonKind.Colorist},
            {"Colouring Assistance", PersonKind.Colorist},
            {"Colouring Chief", PersonKind.Colorist},
            {"Colouring Direction", PersonKind.Director},
            {"Colouring Inspection", PersonKind.Colorist},
            {"Colourist", PersonKind.Colorist},
            {"Comic Editor", PersonKind.Editor},
            {"Commentary Recording", PersonKind.Artist},
            {"Commodity Manager", PersonKind.Producer},
            {"Companion Design", PersonKind.Illustrator},
            {"Composer", PersonKind.Composer},
            {"Composite Coordinator", PersonKind.Producer},
            {"Composite Lead", PersonKind.Artist},
            {"Composition", PersonKind.Artist},
            {"Composition Assistance", PersonKind.Artist},
            {"Composition Director", PersonKind.Artist},
            {"Composition Engineer", PersonKind.Artist},
            {"Composition Planner", PersonKind.Artist},
            {"Composition Supervisor", PersonKind.Artist},
            {"Computer Graphics", PersonKind.Artist},
            {"Computer Processing", PersonKind.Artist},
            {"Computer Programming", PersonKind.Artist},
            {"Concept Advisor", PersonKind.Artist},
            {"Concept Art", PersonKind.Artist},
            {"Concept Art Design", PersonKind.Illustrator},
            {"Concept Art Director", PersonKind.Director},
            {"Concept Assistance", PersonKind.Artist},
            {"Concept Board", PersonKind.Artist},
            {"Concept Composite", PersonKind.Artist},
            {"Concept Design", PersonKind.Illustrator},
            {"Concept Design Assistance", PersonKind.Illustrator},
            {"Concept Director", PersonKind.Director},
            {"Concept Layout", PersonKind.Artist},
            {"Concept Photograph", PersonKind.Artist},
            {"Concept Planning", PersonKind.Producer},
            {"Conducting", PersonKind.Artist},
            {"Content Director", PersonKind.Director},
            {"Content Manager", PersonKind.Producer},
            {"Content Planning", PersonKind.Producer},
            {"Content Producer", PersonKind.Producer},
            {"Contents Business", PersonKind.Artist},
            {"Contract Manager", PersonKind.Producer},
            {"Coordinating Producer", PersonKind.Producer},
            {"Coordinator", PersonKind.Producer},
            {"Copyright Assistance", PersonKind.Artist},
            {"Copyright Manager", PersonKind.Producer},
            {"Copyright Work", PersonKind.Artist},
            {"Copyright Work Assistance", PersonKind.Artist},
            {"Costume Concept Design", PersonKind.Illustrator},
            {"Costume Design", PersonKind.Illustrator},
            {"Costume Design Assistance", PersonKind.Illustrator},
            {"Coverage", PersonKind.CoverArtist},
            {"Coverage Assistance", PersonKind.CoverArtist},
            {"Coverage Coordinator", PersonKind.Producer},
            {"Coverage Map", PersonKind.CoverArtist},
            {"Coverage Photography", PersonKind.CoverArtist},
            {"Crayon Drawings", PersonKind.Artist},
            {"Creative Advisor", PersonKind.Artist},
            {"Creative Animation Direction", PersonKind.Director},
            {"Creative Consultant", PersonKind.Artist},
            {"Creative Coordinate", PersonKind.Artist},
            {"Creative Director", PersonKind.Director},
            {"Creative Manager", PersonKind.Producer},
            {"Creative Officer", PersonKind.Artist},
            {"Creative Producer", PersonKind.Producer},
            {"Creative Supervision", PersonKind.Director},
            {"Creator", PersonKind.Artist},
            {"Creature Animation Direction", PersonKind.Director},
            {"Creature Design", PersonKind.Illustrator},
            {"Creature Design Assistance", PersonKind.Illustrator},
            {"Creature Effects Design", PersonKind.Illustrator},
            {"Crowd Simulation Assistance", PersonKind.Artist},
            {"Cut-out Animation", PersonKind.Artist},
            {"Cut-out Animation Manager", PersonKind.Producer},
            {"Cut-out Picture Artist", PersonKind.Artist},
            {"Cutout Work", PersonKind.Artist},
            {"DTS Digital Mastering", PersonKind.Artist},
            {"DTS Encoding", PersonKind.Artist},
            {"DTS Mastering", PersonKind.Artist},
            {"DVD Producer", PersonKind.Producer},
            {"Dance", PersonKind.Artist},
            {"Dance Assistance", PersonKind.Artist},
            {"Dance Motion", PersonKind.Artist},
            {"Dance Motion Assistance", PersonKind.Artist},
            {"Dance Supervision", PersonKind.Director},
            {"Dancing Animation", PersonKind.Artist},
            {"Dancing CG Direction", PersonKind.Director},
            {"Dancing Choreography", PersonKind.Artist},
            {"Dancing Choreography Assistance", PersonKind.Artist},
            {"Dancing Choreography Direction", PersonKind.Director},
            {"Dancing Choreography Direction Assistance", PersonKind.Director},
            {"Data Broadcasting Staff", PersonKind.Artist},
            {"Data Check", PersonKind.Artist},
            {"Data Confirmation", PersonKind.Artist},
            {"Data Confirmation Supervision", PersonKind.Director},
            {"Data Conversion", PersonKind.Artist},
            {"Data Coordination", PersonKind.Artist},
            {"Data Management", PersonKind.Producer},
            {"Design", PersonKind.Illustrator},
            {"Design Advisor", PersonKind.Illustrator},
            {"Design Assistance", PersonKind.Illustrator},
            {"Design Coordinator", PersonKind.Producer},
            {"Design Copy", PersonKind.Illustrator},
            {"Design Desk", PersonKind.Illustrator},
            {"Design Development", PersonKind.Illustrator},
            {"Design Development Assistance", PersonKind.Illustrator},
            {"Design Director", PersonKind.Director},
            {"Design Generalisation", PersonKind.Illustrator},
            {"Design Lead", PersonKind.Illustrator},
            {"Design Manager", PersonKind.Producer},
            {"Design Manager Assistance", PersonKind.Producer},
            {"Design Research", PersonKind.Illustrator},
            {"Design Supervision", PersonKind.Director},
            {"Design Supervision Assistance", PersonKind.Director},
            {"Design Works", PersonKind.Illustrator},
            {"Design Works Assistance", PersonKind.Illustrator},
            {"Detail Works", PersonKind.Artist},
            {"Development Assistance", PersonKind.Artist},
            {"Development Assistant", PersonKind.Artist},
            {"Development Manager", PersonKind.Producer},
            {"Dialect Assistance", PersonKind.Artist},
            {"Dialect Guidance", PersonKind.Artist},
            {"Dialect Supervision", PersonKind.Director},
            {"Dialogue Director", PersonKind.Director},
            {"Dialogue Editing", PersonKind.Writer},
            {"Dialogue Editing Assistant", PersonKind.Writer},
            {"Dialogue Recording", PersonKind.Writer},
            {"Dialogue Recording Assistance", PersonKind.Writer},
            {"Dialogue Recording Studio", PersonKind.Writer},
            {"Dialogue Supervision", PersonKind.Director},
            {"Digital & Film Lab", PersonKind.Artist},
            {"Digital Advisor", PersonKind.Artist},
            {"Digital Animation", PersonKind.Artist},
            {"Digital Animation Assistance", PersonKind.Artist},
            {"Digital Animation Chief Manager", PersonKind.Producer},
            {"Digital Animation Direction", PersonKind.Director},
            {"Digital Animation Manager", PersonKind.Producer},
            {"Digital Art", PersonKind.Artist},
            {"Digital Art Support", PersonKind.Artist},
            {"Digital Art Works", PersonKind.Artist},
            {"Digital Assistant Production Manager", PersonKind.Producer},
            {"Digital Authoring", PersonKind.Author},
            {"Digital Background Art", PersonKind.Artist},
            {"Digital Background Processing", PersonKind.Artist},
            {"Digital Betacam Recording", PersonKind.Artist},
            {"Digital Cel Inspection", PersonKind.Artist},
            {"Digital Chief", PersonKind.Artist},
            {"Digital Cinema Encoding", PersonKind.Artist},
            {"Digital Cinema Engineer", PersonKind.Engineer},
            {"Digital Cinema Lab", PersonKind.Artist},
            {"Digital Cinema Mastering", PersonKind.Artist},
            {"Digital Cinema Mastering Studio", PersonKind.Artist},
            {"Digital Cinema Package", PersonKind.Artist},
            {"Digital Cinema Package Compressionist", PersonKind.Artist},
            {"Digital Cinema Package Mastering", PersonKind.Artist},
            {"Digital Cinema Studio", PersonKind.Artist},
            {"Digital Colour Grading", PersonKind.Colorist},
            {"Digital Colouring", PersonKind.Colorist},
            {"Digital Colouring Chief", PersonKind.Colorist},
            {"Digital Colouring Inspection", PersonKind.Colorist},
            {"Digital Colouring Work", PersonKind.Colorist},
            {"Digital Coordination", PersonKind.Artist},
            {"Digital Correction", PersonKind.Artist},
            {"Digital Design", PersonKind.Illustrator},
            {"Digital Direction Assistance", PersonKind.Director},
            {"Digital Director", PersonKind.Director},
            {"Digital Editing", PersonKind.Editor},
            {"Digital Editorial Services", PersonKind.Editor},
            {"Digital Effects", PersonKind.Artist},
            {"Digital Effects Animator", PersonKind.Artist},
            {"Digital Effects Assistance", PersonKind.Artist},
            {"Digital Effects Photography", PersonKind.Artist},
            {"Digital Effects Supervisor", PersonKind.Director},
            {"Digital Film Recording", PersonKind.Artist},
            {"Digital Generalisation", PersonKind.Artist},
            {"Digital Graphics", PersonKind.Artist},
            {"Digital Harmony", PersonKind.Artist},
            {"Digital Image Processing", PersonKind.Artist},
            {"Digital In-Between Animation", PersonKind.Artist},
            {"Digital In-Between Animation Check", PersonKind.Artist},
            {"Digital In-Between Animation Inspection", PersonKind.Artist},
            {"Digital In-Between/Finishing", PersonKind.Artist},
            {"Digital In-Between/Finishing Assistance", PersonKind.Artist},
            {"Digital Ink & Paint", PersonKind.Inker},
            {"Digital Key Animation", PersonKind.Artist},
            {"Digital Key Animation Assistance", PersonKind.Artist},
            {"Digital Lab", PersonKind.Artist},
            {"Digital Line Producer", PersonKind.Producer},
            {"Digital Mastering", PersonKind.Artist},
            {"Digital Materials", PersonKind.Artist},
            {"Digital Media Conversion", PersonKind.Artist},
            {"Digital Media Conversion Assistance", PersonKind.Artist},
            {"Digital Noise Raw Material Photography", PersonKind.Artist},
            {"Digital Operator", PersonKind.Artist},
            {"Digital Optical Recording", PersonKind.Artist},
            {"Digital Paint Artist", PersonKind.Artist},
            {"Digital Part", PersonKind.Artist},
            {"Digital Photographic Direction", PersonKind.Director},
            {"Digital Photographic Direction Assistance", PersonKind.Director},
            {"Digital Photography", PersonKind.Artist},
            {"Digital Photography Assistance", PersonKind.Artist},
            {"Digital Photography Chief", PersonKind.Artist},
            {"Digital Producer", PersonKind.Producer},
            {"Digital Production Manager", PersonKind.Producer},
            {"Digital Raw Material Scanning", PersonKind.Artist},
            {"Digital Re-recording", PersonKind.Artist},
            {"Digital Recording", PersonKind.Artist},
            {"Digital Retouch", PersonKind.Artist},
            {"Digital Supervision", PersonKind.Director},
            {"Digital Transfer", PersonKind.Artist},
            {"Digital Video Supervision", PersonKind.Director},
            {"Digital Work", PersonKind.Artist},
            {"Digital Work Assistance", PersonKind.Artist},
            {"Digital Work Assistant", PersonKind.Artist},
            {"Digital Work Desk", PersonKind.Artist},
            {"Digital Work Manager", PersonKind.Producer},
            {"Direction Check Assistant", PersonKind.Director},
            {"Direction Management Assistance", PersonKind.Director},
            {"Display Design", PersonKind.Illustrator},
            {"Distribution", PersonKind.Artist},
            {"Distribution Assistance", PersonKind.Artist},
            {"Distribution Coordination", PersonKind.Artist},
            {"Distribution Generalisation", PersonKind.Artist},
            {"Distribution Licence", PersonKind.Artist},
            {"Distribution Licence Manager", PersonKind.Producer},
            {"Distribution Manager", PersonKind.Producer},
            {"Distribution Publicity", PersonKind.Artist},
            {"Distribution Publicity Assistance", PersonKind.Artist},
            {"Distribution Sales", PersonKind.Artist},
            {"Dolby Digital", PersonKind.Artist},
            {"Dolby Digital Consultant", PersonKind.Artist},
            {"Dolby Film Consultant", PersonKind.Artist},
            {"Dolby Stereo Consultant", PersonKind.Artist},
            {"Domestic Broadcast Sales", PersonKind.Artist},
            {"Domestic Licence", PersonKind.Artist},
            {"Domestic Licence Assistance", PersonKind.Artist},
            {"Draft Modelling", PersonKind.Artist},
            {"Dramatisation", PersonKind.Artist},
            {"Drums", PersonKind.Artist},
            {"Dubbing Assistant", PersonKind.Artist},
            {"Dubbing Stage", PersonKind.Artist},
            {"Dubbing Studio", PersonKind.Artist},
            {"Editing", PersonKind.Editor},
            {"Editing Assistant", PersonKind.Editor},
            {"Editing Coordinator", PersonKind.Producer},
            {"Editing Desk", PersonKind.Editor},
            {"Editing Director", PersonKind.Director},
            {"Editing Manager", PersonKind.Producer},
            {"Editing Operator", PersonKind.Editor},
            {"Editing Studio", PersonKind.Editor},
            {"Editing Studio Assistance", PersonKind.Editor},
            {"Editing Studio Manager", PersonKind.Producer},
            {"Editor", PersonKind.Editor},
            {"Editorial Assistance", PersonKind.Editor},
            {"Effect Design", PersonKind.Illustrator},
            {"Effect Design Assistance", PersonKind.Illustrator},
            {"Effect Development", PersonKind.Artist},
            {"Effect Works", PersonKind.Artist},
            {"Effector", PersonKind.Artist},
            {"Effects", PersonKind.Artist},
            {"Effects Animation", PersonKind.Artist},
            {"Effects Animation Direction", PersonKind.Director},
            {"Effects Animation Direction Assistance", PersonKind.Director},
            {"Effects Chief", PersonKind.Artist},
            {"Electric Bass", PersonKind.Artist},
            {"Electric Guitar", PersonKind.Artist},
            {"Emblem Design", PersonKind.Illustrator},
            {"Emono Design", PersonKind.Illustrator},
            {"End Card", PersonKind.Artist},
            {"End Card Assistance", PersonKind.Artist},
            {"End Card Colouring", PersonKind.Colorist},
            {"End Card Photography", PersonKind.Artist},
            {"End Card Producer", PersonKind.Producer},
            {"Ending", PersonKind.Artist},
            {"Ending Animation", PersonKind.Artist},
            {"Ending Animation Assistance", PersonKind.Artist},
            {"Ending Animation Direction", PersonKind.Director},
            {"Ending Animation Manager", PersonKind.Producer},
            {"Ending Assistance", PersonKind.Artist},
            {"Ending CG", PersonKind.Artist},
            {"Ending Character Animation Direction", PersonKind.Director},
            {"Ending Chief Animation Direction", PersonKind.Director},
            {"Ending Colour Coordinator", PersonKind.Producer},
            {"Ending Colour Design", PersonKind.Colorist},
            {"Ending Coordinator", PersonKind.Producer},
            {"Ending Designer", PersonKind.Illustrator},
            {"Ending Direction", PersonKind.Director},
            {"Ending Editing", PersonKind.Editor},
            {"Ending Effects", PersonKind.Artist},
            {"Ending Graphic Design", PersonKind.Illustrator},
            {"Ending Graphics", PersonKind.Artist},
            {"Ending Illustration", PersonKind.Illustrator},
            {"Ending Illustration Finishing/Clean-up", PersonKind.Illustrator},
            {"Ending Mechanical Animation Direction", PersonKind.Director},
            {"Ending Music Work Assistance", PersonKind.Composer},
            {"Ending Paint", PersonKind.Artist},
            {"Ending Photograph Assistance", PersonKind.Artist},
            {"Ending Photography", PersonKind.Artist},
            {"Ending Processing", PersonKind.Artist},
            {"Ending Staff", PersonKind.Artist},
            {"Ending Storyboard", PersonKind.Director},
            {"Ending Supervision", PersonKind.Director},
            {"Ending Support", PersonKind.Artist},
            {"Ending Telop", PersonKind.Artist},
            {"Ending Theme Choreography", PersonKind.Artist},
            {"Ending Theme Music Director", PersonKind.Director},
            {"Ending Theme Music Work Assistance", PersonKind.Composer},
            {"Ending Theme Producer", PersonKind.Producer},
            {"Ending Theme Work", PersonKind.Artist},
            {"Ending Theme Work Assistance", PersonKind.Artist},
            {"Ending Theme Work Manager", PersonKind.Producer},
            {"Ending Title", PersonKind.Artist},
            {"Ending Video Direction", PersonKind.Director},
            {"Ending Video Work", PersonKind.Artist},
            {"Ending Video Work Editing", PersonKind.Editor},
            {"Ending Work", PersonKind.Artist},
            {"Ending Work Manager", PersonKind.Producer},
            {"Engineering Management", PersonKind.Engineer},
            {"Engineering Services", PersonKind.Engineer},
            {"English Subtitles", PersonKind.Translator},
            {"English Translation", PersonKind.Translator},
            {"Episode CG Direction", PersonKind.Director},
            {"Episode Direction", PersonKind.Director},
            {"Episode Direction Composition", PersonKind.Artist},
            {"Episode Direction Supervision", PersonKind.Director},
            {"Episode Generalisation", PersonKind.Artist},
            {"Equipment Assistance", PersonKind.Artist},
            {"Equipment Design", PersonKind.Illustrator},
            {"Event Manager", PersonKind.Producer},
            {"Event Promotor", PersonKind.Artist},
            {"Event Work", PersonKind.Artist},
            {"Everything Else", PersonKind.Artist},
            {"Executive Coordinator", PersonKind.Producer},
            {"Executive Marketing Producer", PersonKind.Producer},
            {"Executive Music Producer", PersonKind.Producer},
            {"Executive Producer", PersonKind.Producer},
            {"Executive Supervision", PersonKind.Director},
            {"Eyecatch", PersonKind.Artist},
            {"Eyecatch Animation Direction", PersonKind.Director},
            {"Eyecatch Assistance", PersonKind.Artist},
            {"Eyecatch CG", PersonKind.Artist},
            {"Eyecatch Calligraphy", PersonKind.Artist},
            {"Eyecatch Concept", PersonKind.Artist},
            {"Eyecatch Direction", PersonKind.Director},
            {"Eyecatch Layout", PersonKind.Artist},
            {"Eyecatch Manager", PersonKind.Producer},
            {"Eyecatch Storyboard", PersonKind.Director},
            {"Eyecatch Supervisor", PersonKind.Director},
            {"Eyecatch Voice Work Assistance", PersonKind.Actor},
            {"FX Supervisor", PersonKind.Director},
            {"Fashion Coordinator", PersonKind.Producer},
            {"Fashion Design", PersonKind.Illustrator},
            {"Featured Novel", PersonKind.Artist},
            {"Figure Assistance", PersonKind.Artist},
            {"Film", PersonKind.Artist},
            {"Film Coordinator", PersonKind.Producer},
            {"Film Director", PersonKind.Director},
            {"Film Editing", PersonKind.Editor},
            {"Film Editing Assistant", PersonKind.Editor},
            {"Film Editing Coordinator", PersonKind.Producer},
            {"Film Editing Desk", PersonKind.Editor},
            {"Film Editing Manager", PersonKind.Producer},
            {"Film Editing Studio", PersonKind.Editor},
            {"Film Festival Distribution", PersonKind.Artist},
            {"Film I/O", PersonKind.Artist},
            {"Film Manager", PersonKind.Producer},
            {"Film Processing", PersonKind.Artist},
            {"Film Recording", PersonKind.Artist},
            {"Film Scanning", PersonKind.Artist},
            {"Final Check", PersonKind.Artist},
            {"Finance", PersonKind.Artist},
            {"Finish Work", PersonKind.Artist},
            {"Finishing Editor", PersonKind.Editor},
            {"Finishing/Clean-up", PersonKind.Artist},
            {"Finishing/Clean-up Assistance", PersonKind.Artist},
            {"Finishing/Clean-up Check", PersonKind.Artist},
            {"Finishing/Clean-up Direction", PersonKind.Director},
            {"Finishing/Clean-up Inspection", PersonKind.Artist},
            {"Finishing/Clean-up Inspection Assistance", PersonKind.Artist},
            {"Finishing/Clean-up Manager", PersonKind.Producer},
            {"Finishing/Clean-up Processing", PersonKind.Artist},
            {"Finishing/Clean-up Studio", PersonKind.Artist},
            {"Finishing/Clean-up Supervision", PersonKind.Director},
            {"Finishing/Clean-up Work", PersonKind.Artist},
            {"Flash Animation", PersonKind.Artist},
            {"Flash Animation Operator", PersonKind.Artist},
            {"Flash Character Design", PersonKind.Illustrator},
            {"Floor Director", PersonKind.Director},
            {"Floor Manager", PersonKind.Producer},
            {"Floor Planner", PersonKind.Artist},
            {"Foley", PersonKind.Artist},
            {"Foley Assistant", PersonKind.Artist},
            {"Foley Design", PersonKind.Illustrator},
            {"Foley Editor", PersonKind.Editor},
            {"Foley Material Assistance", PersonKind.Artist},
            {"Foley Mixing", PersonKind.Mixer},
            {"Foley Recording", PersonKind.Artist},
            {"Foley Studio", PersonKind.Artist},
            {"Font", PersonKind.Artist},
            {"Font Assistance", PersonKind.Artist},
            {"Font Design", PersonKind.Illustrator},
            {"Font Design Assistance", PersonKind.Illustrator},
            {"Front Sponsor Screen Illustration", PersonKind.Illustrator},
            {"Future Visual", PersonKind.Artist},
            {"Game Assistance", PersonKind.Artist},
            {"Game Creative Director", PersonKind.Director},
            {"Game Design", PersonKind.Illustrator},
            {"Game Graphics Creator", PersonKind.Artist},
            {"Game Illustration", PersonKind.Illustrator},
            {"Game Layout Design", PersonKind.Illustrator},
            {"Game Music Work", PersonKind.Composer},
            {"Game Scene Work", PersonKind.Artist},
            {"Game Work Staff", PersonKind.Artist},
            {"Gekimation", PersonKind.Artist},
            {"General Animation Producer", PersonKind.Producer},
            {"General Manager", PersonKind.Producer},
            {"General Producer", PersonKind.Producer},
            {"General Publicity", PersonKind.Artist},
            {"Graphic Advisor", PersonKind.Artist},
            {"Graphic Design", PersonKind.Illustrator},
            {"Graphic Design Assistance", PersonKind.Illustrator},
            {"Graphic Design Director", PersonKind.Director},
            {"Graphic Work", PersonKind.Artist},
            {"Graphics", PersonKind.Artist},
            {"Graphics Assistance", PersonKind.Artist},
            {"Graphics Authoring", PersonKind.Author},
            {"Graphics Operation", PersonKind.Artist},
            {"Guest Art Direction", PersonKind.Director},
            {"Guest Character Animation Direction", PersonKind.Director},
            {"Guest Character Design", PersonKind.Illustrator},
            {"Guest Character Design Assistance", PersonKind.Illustrator},
            {"Guest Clothing Design", PersonKind.Illustrator},
            {"Guest Colour Design", PersonKind.Colorist},
            {"Guest Composer", PersonKind.Composer},
            {"Guest Costume Design", PersonKind.Illustrator},
            {"Guest Creature Design", PersonKind.Illustrator},
            {"Guest Design", PersonKind.Illustrator},
            {"Guest Design Assistance", PersonKind.Illustrator},
            {"Guest Key Animation", PersonKind.Artist},
            {"Guest Mechanical Art Design", PersonKind.Illustrator},
            {"Guest Mechanical Design", PersonKind.Illustrator},
            {"Guest Modelling", PersonKind.Artist},
            {"Guest Musician", PersonKind.Composer},
            {"Guest Prop Design", PersonKind.Illustrator},
            {"Guitar", PersonKind.Artist},
            {"HD Colour Correction", PersonKind.Colorist},
            {"HD Colour Grading", PersonKind.Colorist},
            {"HD Coordinating Manager", PersonKind.Producer},
            {"HD Coordinator", PersonKind.Producer},
            {"HD Editing", PersonKind.Editor},
            {"HD Editing Assistant", PersonKind.Editor},
            {"HD Editing Coordinator", PersonKind.Producer},
            {"HD Editing Desk", PersonKind.Editor},
            {"HD Editing Manager", PersonKind.Producer},
            {"HD Editing Studio", PersonKind.Editor},
            {"HD Editing Studio Desk", PersonKind.Editor},
            {"HD Editing Work", PersonKind.Editor},
            {"HD Grading", PersonKind.Artist},
            {"HD Laser Cinema", PersonKind.Artist},
            {"HD Linear Editing", PersonKind.Editor},
            {"HD Photography Coordinator", PersonKind.Producer},
            {"HD Real Time Recording", PersonKind.Artist},
            {"HD Recording", PersonKind.Artist},
            {"HD Remastering", PersonKind.Artist},
            {"HD Telecine", PersonKind.Artist},
            {"Hairpiece/Wig", PersonKind.Artist},
            {"Handwriting", PersonKind.Artist},
            {"Handwriting Design", PersonKind.Illustrator},
            {"Hardware Assistance", PersonKind.Artist},
            {"Harmony", PersonKind.Artist},
            {"Harmony Processing", PersonKind.Artist},
            {"Head of Technology", PersonKind.Artist},
            {"History Research", PersonKind.Writer},
            {"Hybrid Cap", PersonKind.Artist},
            {"IP Director", PersonKind.Director},
            {"Illustration", PersonKind.Illustrator},
            {"Illustration Assistance", PersonKind.Illustrator},
            {"Illustration Processing", PersonKind.Illustrator},
            {"Illustration Supervision", PersonKind.Director},
            {"Image Art", PersonKind.Artist},
            {"Image Board", PersonKind.Artist},
            {"Image Correction", PersonKind.Artist},
            {"Image Design", PersonKind.Illustrator},
            {"Image Direction", PersonKind.Director},
            {"Image Editing", PersonKind.Editor},
            {"Image Illustration", PersonKind.Illustrator},
            {"Image Leader", PersonKind.Artist},
            {"Image Provider", PersonKind.Artist},
            {"Image Sketch", PersonKind.Artist},
            {"In-Between Animation", PersonKind.Artist},
            {"In-Between Animation Assistance", PersonKind.Artist},
            {"In-Between Animation Assistance Studio", PersonKind.Artist},
            {"In-Between Animation Assistant", PersonKind.Artist},
            {"In-Between Animation Check", PersonKind.Artist},
            {"In-Between Animation Check Assistant", PersonKind.Artist},
            {"In-Between Animation Chief", PersonKind.Artist},
            {"In-Between Animation Inspection", PersonKind.Artist},
            {"In-Between Animation Inspection Assistance", PersonKind.Artist},
            {"In-Between Animation Inspection Chief", PersonKind.Artist},
            {"In-Between Animation Manager", PersonKind.Producer},
            {"In-Between Animation Studio", PersonKind.Artist},
            {"In-Between Animation Supervision", PersonKind.Director},
            {"In-Between/Finishing", PersonKind.Artist},
            {"In-Between/Finishing Assistance", PersonKind.Artist},
            {"In-Between/Finishing Management Assistance", PersonKind.Producer},
            {"In-Between/Finishing Manager", PersonKind.Producer},
            {"In-Between/Finishing Work", PersonKind.Artist},
            {"In-Between/Finishing Work Assistance", PersonKind.Artist},
            {"Infrastructure Management", PersonKind.Producer},
            {"Insert Song", PersonKind.Artist},
            {"Insert Song Assistance", PersonKind.Artist},
            {"Insert Song Producer", PersonKind.Producer},
            {"Inspection", PersonKind.Artist},
            {"Instrument Assistance", PersonKind.Artist},
            {"Instrument Programming", PersonKind.Artist},
            {"Instrument Supervision", PersonKind.Director},
            {"Interpretation", PersonKind.Artist},
            {"Item Planning", PersonKind.Producer},
            {"Japanese Language Interpretation", PersonKind.Artist},
            {"Japanese Language Subtitle Work", PersonKind.Translator},
            {"Japanese Pattern Material Assistance", PersonKind.Artist},
            {"Kendo Animation Direction Assistance", PersonKind.Director},
            {"Key & In-Between Animation", PersonKind.Artist},
            {"Key Animation", PersonKind.Artist},
            {"Key Animation Assistance", PersonKind.Artist},
            {"Key Animation Check", PersonKind.Artist},
            {"Key Animation Check Assistant", PersonKind.Artist},
            {"Key Animation Correction", PersonKind.Artist},
            {"Key Animation Work Assistance", PersonKind.Artist},
            {"Key Animation/Finishing Manager", PersonKind.Producer},
            {"Key Visual", PersonKind.Artist},
            {"Key Visual Art Director", PersonKind.Director},
            {"Key Visual Work", PersonKind.Artist},
            {"Kinescope", PersonKind.Artist},
            {"Korean Subtitles", PersonKind.Translator},
            {"Lab", PersonKind.Artist},
            {"Lab Coordinator", PersonKind.Producer},
            {"Lab Desk", PersonKind.Artist},
            {"Lab Manager", PersonKind.Producer},
            {"Lab Producer", PersonKind.Producer},
            {"Language Coordination", PersonKind.Artist},
            {"Language Manager", PersonKind.Producer},
            {"Language Supervision", PersonKind.Director},
            {"Layout", PersonKind.Artist},
            {"Layout Animation", PersonKind.Artist},
            {"Layout Animation Direction", PersonKind.Director},
            {"Layout Animation Supervision", PersonKind.Director},
            {"Layout Assistance", PersonKind.Artist},
            {"Layout Check", PersonKind.Artist},
            {"Layout Check Assistant", PersonKind.Artist},
            {"Layout Composition", PersonKind.Artist},
            {"Layout Correction", PersonKind.Artist},
            {"Layout Correction Assistance", PersonKind.Artist},
            {"Layout Design", PersonKind.Illustrator},
            {"Layout Inspection", PersonKind.Artist},
            {"Layout Processing Assistance", PersonKind.Artist},
            {"Layout Supervision", PersonKind.Director},
            {"Layout Supervision Assistance", PersonKind.Director},
            {"Lead Animator", PersonKind.Artist},
            {"Lead Artist", PersonKind.Artist},
            {"Lead CG Animator", PersonKind.Artist},
            {"Lead CG Background Artist", PersonKind.Artist},
            {"Lead CGI Designer", PersonKind.Illustrator},
            {"Lead Engineer", PersonKind.Engineer},
            {"Lead Foley Artist", PersonKind.Artist},
            {"Lead Mecha Designer", PersonKind.Illustrator},
            {"Legal Advisor", PersonKind.Artist},
            {"Legal Affairs Manager", PersonKind.Producer},
            {"Leica Reel", PersonKind.Artist},
            {"Letter Design", PersonKind.Letterer},
            {"Letter Design Assistance", PersonKind.Letterer},
            {"Lettering", PersonKind.Letterer},
            {"Licence Coordinator", PersonKind.Producer},
            {"Licence Generalisation", PersonKind.Artist},
            {"Licence Manager", PersonKind.Producer},
            {"Licence Sales", PersonKind.Artist},
            {"Licencing", PersonKind.Artist},
            {"Licencing Assistance", PersonKind.Artist},
            {"Lighting", PersonKind.Artist},
            {"Lighting Assistance", PersonKind.Artist},
            {"Lighting Design", PersonKind.Illustrator},
            {"Lighting Effects", PersonKind.Artist},
            {"Line Coordination", PersonKind.Artist},
            {"Line Design", PersonKind.Illustrator},
            {"Line Director", PersonKind.Director},
            {"Line Manager", PersonKind.Producer},
            {"Line Photography", PersonKind.Artist},
            {"Line Producer", PersonKind.Producer},
            {"Line Test", PersonKind.Artist},
            {"Line Test Assistance", PersonKind.Artist},
            {"Literature Producer", PersonKind.Producer},
            {"Lith Work", PersonKind.Artist},
            {"Live Action Art", PersonKind.Artist},
            {"Live Action Art Assistance", PersonKind.Artist},
            {"Live Action Part Work Assistance", PersonKind.Artist},
            {"Live Action Photography", PersonKind.Artist},
            {"Live Action Photography Assistance", PersonKind.Artist},
            {"Live Action Photography Director", PersonKind.Director},
            {"Live Action Processing", PersonKind.Artist},
            {"Live Choreography", PersonKind.Artist},
            {"Live Choreography Assistance", PersonKind.Artist},
            {"Live Part Direction", PersonKind.Director},
            {"Live Scene Assistance", PersonKind.Artist},
            {"Local Publicity", PersonKind.Artist},
            {"Location", PersonKind.Artist},
            {"Location Assistance", PersonKind.Artist},
            {"Location Facilities Assistance", PersonKind.Artist},
            {"Location Hunting", PersonKind.Artist},
            {"Location Hunting Assistance", PersonKind.Artist},
            {"Location Hunting Coordinator", PersonKind.Producer},
            {"Location Modelling", PersonKind.Artist},
            {"Logo Design", PersonKind.Illustrator},
            {"Logo Design Assistance", PersonKind.Illustrator},
            {"Look Development", PersonKind.Artist},
            {"Look Development Chief", PersonKind.Artist},
            {"Look Development Supervisor", PersonKind.Director},
            {"Lyrics", PersonKind.Lyricist},
            {"MA Operator", PersonKind.Artist},
            {"MMD Modelling", PersonKind.Artist},
            {"MMD Work Assistance", PersonKind.Artist},
            {"Main Animator", PersonKind.Artist},
            {"Main Art Design", PersonKind.Illustrator},
            {"Main Character Colour Design", PersonKind.Colorist},
            {"Main Character Design", PersonKind.Illustrator},
            {"Main Character Layout Supervision", PersonKind.Director},
            {"Main Character Supervision", PersonKind.Director},
            {"Main Colour Design", PersonKind.Colorist},
            {"Main Composer", PersonKind.Composer},
            {"Main Creature Design", PersonKind.Illustrator},
            {"Main Design", PersonKind.Illustrator},
            {"Main Design Works", PersonKind.Illustrator},
            {"Main Illustrator", PersonKind.Illustrator},
            {"Main Logo Design", PersonKind.Illustrator},
            {"Main Mechanical Design", PersonKind.Illustrator},
            {"Main Production", PersonKind.Producer},
            {"Main Prop Design", PersonKind.Illustrator},
            {"Main Title Animation", PersonKind.Artist},
            {"Main Title CG Animation", PersonKind.Artist},
            {"Main Title Composition", PersonKind.Artist},
            {"Main Title Design", PersonKind.Illustrator},
            {"Main Title Font", PersonKind.Artist},
            {"Main Title Photography", PersonKind.Artist},
            {"Main Title Work", PersonKind.Artist},
            {"Main Writer", PersonKind.Artist},
            {"Making Staff", PersonKind.Artist},
            {"Making of Work", PersonKind.Artist},
            {"Management", PersonKind.Producer},
            {"Management Assistance", PersonKind.Producer},
            {"Management Chief", PersonKind.Producer},
            {"Manager", PersonKind.Producer},
            {"Managing Director", PersonKind.Director},
            {"Manga", PersonKind.Artist},
            {"Manga Assistance", PersonKind.Artist},
            {"Manipulator", PersonKind.Artist},
            {"Map Data Assistance", PersonKind.Artist},
            {"Map Design", PersonKind.Illustrator},
            {"Marketing", PersonKind.Artist},
            {"Marketing Assistance", PersonKind.Artist},
            {"Marketing Coordination", PersonKind.Artist},
            {"Marketing Director", PersonKind.Director},
            {"Marketing Producer", PersonKind.Producer},
            {"Marketing Supervision", PersonKind.Director},
            {"Mastering Director", PersonKind.Director},
            {"Mastering Engineer", PersonKind.Engineer},
            {"Mastering Manager", PersonKind.Producer},
            {"Mastering Studio", PersonKind.Artist},
            {"Material Design", PersonKind.Illustrator},
            {"Material Editing", PersonKind.Editor},
            {"Material Processing", PersonKind.Artist},
            {"Materials/Data", PersonKind.Artist},
            {"Materials/Data Assistance", PersonKind.Artist},
            {"Materials/Data Sponsoring", PersonKind.Artist},
            {"Matte Paint Director", PersonKind.Director},
            {"Matte Painting", PersonKind.Artist},
            {"Mechanical Accessory Design", PersonKind.Illustrator},
            {"Mechanical Accessory Design Assistance", PersonKind.Illustrator},
            {"Mechanical Animation Direction", PersonKind.Director},
            {"Mechanical Animation Direction Assistance", PersonKind.Director},
            {"Mechanical Animation Supervision", PersonKind.Director},
            {"Mechanical Art", PersonKind.Artist},
            {"Mechanical Art Direction", PersonKind.Director},
            {"Mechanical Board", PersonKind.Artist},
            {"Mechanical Character Manager", PersonKind.Producer},
            {"Mechanical Concept Design", PersonKind.Illustrator},
            {"Mechanical Design", PersonKind.Illustrator},
            {"Mechanical Design Assistance", PersonKind.Illustrator},
            {"Mechanical Design Supervision", PersonKind.Director},
            {"Mechanical Design Works", PersonKind.Illustrator},
            {"Mechanical Detail Works", PersonKind.Artist},
            {"Mechanical Direction", PersonKind.Director},
            {"Mechanical Key Animation", PersonKind.Artist},
            {"Mechanical Layout Supervision", PersonKind.Director},
            {"Mechanical Modelling", PersonKind.Artist},
            {"Mechanical Prop Design", PersonKind.Illustrator},
            {"Mechanical Supervision", PersonKind.Director},
            {"Mechanical/Effects Animation Direction", PersonKind.Director},
            {"Mechanical/Effects Animation Direction Assistance", PersonKind.Director},
            {"Media Conversion", PersonKind.Artist},
            {"Media Coordinator", PersonKind.Producer},
            {"Media Generalisation", PersonKind.Artist},
            {"Media Manager", PersonKind.Producer},
            {"Media Writer", PersonKind.Artist},
            {"Menu Design", PersonKind.Illustrator},
            {"Merchandise Development", PersonKind.Artist},
            {"Merchandise Licence", PersonKind.Artist},
            {"Merchandise Sales", PersonKind.Artist},
            {"Merchandising", PersonKind.Artist},
            {"Merchandising Assistance", PersonKind.Artist},
            {"Merchandising Director", PersonKind.Director},
            {"Merchandising Manager", PersonKind.Producer},
            {"Merchandising Producer", PersonKind.Producer},
            {"Military Research", PersonKind.Artist},
            {"Military Supervision", PersonKind.Director},
            {"Military Supervision Assistance", PersonKind.Director},
            {"Miniature Photography", PersonKind.Artist},
            {"Miniature Work", PersonKind.Artist},
            {"Model Design", PersonKind.Illustrator},
            {"Modelling", PersonKind.Artist},
            {"Modelling Assistance", PersonKind.Artist},
            {"Modelling Chief", PersonKind.Artist},
            {"Modelling Chief Designer", PersonKind.Illustrator},
            {"Modelling Director", PersonKind.Director},
            {"Modelling Lead", PersonKind.Artist},
            {"Modelling Sub Director", PersonKind.Director},
            {"Modelling Supervision", PersonKind.Director},
            {"Modelling Work", PersonKind.Artist},
            {"Monitor Advisor", PersonKind.Artist},
            {"Monitor Assistance", PersonKind.Artist},
            {"Monitor CG", PersonKind.Artist},
            {"Monitor Calibration", PersonKind.Artist},
            {"Monitor Concept Design", PersonKind.Illustrator},
            {"Monitor Design", PersonKind.Illustrator},
            {"Monitor Design Assistance", PersonKind.Illustrator},
            {"Monitor Font Design", PersonKind.Illustrator},
            {"Monitor Graphics", PersonKind.Artist},
            {"Monitor Graphics Animation", PersonKind.Artist},
            {"Monitor Graphics Assistance", PersonKind.Artist},
            {"Monitor Graphics Concept", PersonKind.Artist},
            {"Monitor Graphics Design", PersonKind.Illustrator},
            {"Monitor Graphics Director", PersonKind.Director},
            {"Monitor Graphics Production Manager", PersonKind.Producer},
            {"Monitor Processing", PersonKind.Artist},
            {"Monitor Production Manager", PersonKind.Producer},
            {"Monitor Work", PersonKind.Artist},
            {"Monitor Work Assistance", PersonKind.Artist},
            {"Motion Assistance", PersonKind.Artist},
            {"Motion Capture", PersonKind.Artist},
            {"Motion Capture Acting Assistance", PersonKind.Artist},
            {"Motion Capture Actor", PersonKind.Actor},
            {"Motion Capture Actor Casting", PersonKind.Actor},
            {"Motion Capture Assistance", PersonKind.Artist},
            {"Motion Capture Clothing Assistance", PersonKind.Artist},
            {"Motion Capture Coordinator", PersonKind.Producer},
            {"Motion Capture Coordinator Assistance", PersonKind.Producer},
            {"Motion Capture Designer", PersonKind.Illustrator},
            {"Motion Capture Director", PersonKind.Director},
            {"Motion Capture Editing", PersonKind.Editor},
            {"Motion Capture Engineer", PersonKind.Engineer},
            {"Motion Capture Motion Designer", PersonKind.Illustrator},
            {"Motion Capture Photography", PersonKind.Artist},
            {"Motion Capture Photography Staff", PersonKind.Artist},
            {"Motion Capture Producer", PersonKind.Producer},
            {"Motion Capture Studio", PersonKind.Artist},
            {"Motion Capture Technical Director", PersonKind.Director},
            {"Motion Capture Technical Supervisor", PersonKind.Director},
            {"Motion Coordinator", PersonKind.Producer},
            {"Motion Design", PersonKind.Illustrator},
            {"Motion Director", PersonKind.Director},
            {"Motion Graphics", PersonKind.Artist},
            {"Motion Graphics Assistance", PersonKind.Artist},
            {"Motion Supervision", PersonKind.Director},
            {"Motion Work", PersonKind.Artist},
            {"Moulding", PersonKind.Artist},
            {"Moulding Advisor", PersonKind.Artist},
            {"Moulding Assistance", PersonKind.Artist},
            {"Multi-Design Assistance", PersonKind.Illustrator},
            {"Music Advisor", PersonKind.Composer},
            {"Music Arrangement", PersonKind.Arranger},
            {"Music Assistance", PersonKind.Composer},
            {"Music Assistant", PersonKind.Composer},
            {"Music Assistant Engineer", PersonKind.Engineer},
            {"Music Business Affairs", PersonKind.Composer},
            {"Music Composition", PersonKind.Artist},
            {"Music Coordinator", PersonKind.Producer},
            {"Music Copyright", PersonKind.Composer},
            {"Music Design", PersonKind.Composer},
            {"Music Direction", PersonKind.Director},
            {"Music Editing", PersonKind.Composer},
            {"Music Engineer", PersonKind.Engineer},
            {"Music Manager", PersonKind.Producer},
            {"Music Mixer", PersonKind.Mixer},
            {"Music Mixing Studio", PersonKind.Mixer},
            {"Music Part", PersonKind.Composer},
            {"Music Planning", PersonKind.Producer},
            {"Music Producer", PersonKind.Producer},
            {"Music Production", PersonKind.Producer},
            {"Music Production Assistance", PersonKind.Producer},
            {"Music Publicity", PersonKind.Composer},
            {"Music Publicity Manager", PersonKind.Producer},
            {"Music Publicity Producer", PersonKind.Producer},
            {"Music Publishing", PersonKind.Composer},
            {"Music Re-Mastering", PersonKind.Composer},
            {"Music Recording", PersonKind.Composer},
            {"Music Recording Coordinator", PersonKind.Producer},
            {"Music Recording Studio", PersonKind.Composer},
            {"Music Selection", PersonKind.Composer},
            {"Music Special Assistance", PersonKind.Composer},
            {"Music Sponsoring", PersonKind.Composer},
            {"Music Supervision", PersonKind.Director},
            {"Music Supervision Assistance", PersonKind.Director},
            {"Music Supervision Chief", PersonKind.Director},
            {"Music Tie-up", PersonKind.Composer},
            {"Music Tie-up Assistance", PersonKind.Composer},
            {"Music Work", PersonKind.Composer},
            {"Music Work Assistance", PersonKind.Composer},
            {"Music Work Coordination", PersonKind.Composer},
            {"Music Work Direction", PersonKind.Director},
            {"Music Work Manager", PersonKind.Producer},
            {"Music Work Mastering", PersonKind.Composer},
            {"Music Work Producer", PersonKind.Producer},
            {"Musical Instrument Animation Direction", PersonKind.Director},
            {"Musical Instrument Design", PersonKind.Composer},
            {"Musical Performance", PersonKind.Composer},
            {"Musical Performance Choreography", PersonKind.Composer},
            {"Musical Performance Direction", PersonKind.Director},
            {"Musician", PersonKind.Composer},
            {"Musician Coordinator", PersonKind.Producer},
            {"NFT Sales", PersonKind.Artist},
            {"Nameplate Design", PersonKind.Illustrator},
            {"Narration", PersonKind.Actor},
            {"Natural History Media Producer", PersonKind.Writer},
            {"Negative Cutting", PersonKind.Artist},
            {"Negative Cutting Assistant", PersonKind.Artist},
            {"Network Manager", PersonKind.Producer},
            {"Nonlinear Editing", PersonKind.Editor},
            {"Novel", PersonKind.Artist},
            {"Novel Copyright", PersonKind.Artist},
            {"Novel Editing", PersonKind.Editor},
            {"Novelisation", PersonKind.Artist},
            {"OP/ED CG Work Assistance", PersonKind.Artist},
            {"OP/ED Title Direction", PersonKind.Director},
            {"OP/ED Work", PersonKind.Artist},
            {"OP/ED Work Director", PersonKind.Director},
            {"Object Design", PersonKind.Illustrator},
            {"Offline Editing", PersonKind.Editor},
            {"Offline Editing Assistance", PersonKind.Editor},
            {"Offline Editing Desk", PersonKind.Editor},
            {"Offline Editing Studio", PersonKind.Editor},
            {"Online 3D Editing", PersonKind.Editor},
            {"Online Editing", PersonKind.Editor},
            {"Online Editing Assistance", PersonKind.Editor},
            {"Online Editing Coordinator", PersonKind.Producer},
            {"Online Editing Desk", PersonKind.Editor},
            {"Online Editing Manager", PersonKind.Producer},
            {"Online Editing Operator", PersonKind.Editor},
            {"Online Editing Studio", PersonKind.Editor},
            {"Online HD Editing", PersonKind.Editor},
            {"Opening", PersonKind.Artist},
            {"Opening Animation", PersonKind.Artist},
            {"Opening Animation Assistance", PersonKind.Artist},
            {"Opening Animation Direction", PersonKind.Director},
            {"Opening Animation Manager", PersonKind.Producer},
            {"Opening Art Work", PersonKind.Artist},
            {"Opening Assistance", PersonKind.Artist},
            {"Opening CG", PersonKind.Artist},
            {"Opening Character Animation Direction", PersonKind.Director},
            {"Opening Chief Animation Direction", PersonKind.Director},
            {"Opening Clay Crew", PersonKind.Artist},
            {"Opening Clay Crew Assistance", PersonKind.Artist},
            {"Opening Credit Design Works", PersonKind.Illustrator},
            {"Opening Direction", PersonKind.Director},
            {"Opening Editing", PersonKind.Editor},
            {"Opening Illustration", PersonKind.Illustrator},
            {"Opening Image Work", PersonKind.Artist},
            {"Opening Mechanical Animation Direction", PersonKind.Director},
            {"Opening Music Work", PersonKind.Composer},
            {"Opening Music Work Assistance", PersonKind.Composer},
            {"Opening Photography", PersonKind.Artist},
            {"Opening Staff", PersonKind.Artist},
            {"Opening Storyboard", PersonKind.Director},
            {"Opening Telop", PersonKind.Artist},
            {"Opening Telop Assistance", PersonKind.Artist},
            {"Opening Theme Director", PersonKind.Director},
            {"Opening Theme Music Director", PersonKind.Director},
            {"Opening Theme Music Work Assistance", PersonKind.Composer},
            {"Opening Theme Producer", PersonKind.Producer},
            {"Opening Theme Work", PersonKind.Artist},
            {"Opening Theme Work Assistance", PersonKind.Artist},
            {"Opening Theme Work Manager", PersonKind.Producer},
            {"Opening Title", PersonKind.Artist},
            {"Opening Title CG", PersonKind.Artist},
            {"Opening Video Direction", PersonKind.Director},
            {"Opening Work", PersonKind.Artist},
            {"Opening Work Assistance", PersonKind.Artist},
            {"Opening Work Manager", PersonKind.Producer},
            {"Optical CG Effects Assistance", PersonKind.Artist},
            {"Optical Film Compositing", PersonKind.Artist},
            {"Optical Recording", PersonKind.Artist},
            {"Optical Recording Manager", PersonKind.Producer},
            {"Optical Stereo", PersonKind.Artist},
            {"Orchestra Conducting", PersonKind.Artist},
            {"Orchestra Contractor", PersonKind.Actor},
            {"Orchestra Coordinator", PersonKind.Producer},
            {"Orchestra Manager", PersonKind.Producer},
            {"Orchestra Performance", PersonKind.Artist},
            {"Orchestra Recording", PersonKind.Artist},
            {"Orchestration", PersonKind.Artist},
            {"Organisation", PersonKind.Artist},
            {"Organisation Manager", PersonKind.Producer},
            {"Original Accessory Design", PersonKind.Creator},
            {"Original Accessory Design Assistance", PersonKind.Creator},
            {"Original Animation", PersonKind.Creator},
            {"Original Animation Character Design", PersonKind.Creator},
            {"Original Art Design", PersonKind.Creator},
            {"Original Art Plan", PersonKind.Creator},
            {"Original CG Motion Work", PersonKind.Creator},
            {"Original Character Concept Design", PersonKind.Creator},
            {"Original Character Design Assistance", PersonKind.Creator},
            {"Original Choral Music", PersonKind.Creator},
            {"Original Costume Design", PersonKind.Creator},
            {"Original Costume Design Assistance", PersonKind.Creator},
            {"Original Creature Design", PersonKind.Creator},
            {"Original Design", PersonKind.Creator},
            {"Original Guest Character Design", PersonKind.Creator},
            {"Original Illustration", PersonKind.Creator},
            {"Original Logo Design", PersonKind.Creator},
            {"Original Logo Design Assistance", PersonKind.Creator},
            {"Original Main Character Design", PersonKind.Creator},
            {"Original Manga", PersonKind.Creator},
            {"Original Mechanical Design", PersonKind.Creator},
            {"Original Music", PersonKind.Creator},
            {"Original Music Arrange", PersonKind.Creator},
            {"Original Plan", PersonKind.Creator},
            {"Original Plan Assistance", PersonKind.Creator},
            {"Original Plan Supervision", PersonKind.Creator},
            {"Original Planning", PersonKind.Creator},
            {"Original Production Rights", PersonKind.Creator},
            {"Original Score", PersonKind.Creator},
            {"Original Script", PersonKind.Creator},
            {"Original Setting", PersonKind.Creator},
            {"Original Staff", PersonKind.Creator},
            {"Original Story", PersonKind.Creator},
            {"Original Story Assistance", PersonKind.Creator},
            {"Original Sub Character Design", PersonKind.Creator},
            {"Original Title Design", PersonKind.Creator},
            {"Original Work Assistance", PersonKind.Creator},
            {"Original Work Assistant Producer", PersonKind.Creator},
            {"Original Work Coordinator", PersonKind.Creator},
            {"Original Work Design", PersonKind.Creator},
            {"Original Work Development", PersonKind.Creator},
            {"Original Work Editing", PersonKind.Creator},
            {"Original Work Editing Assistance", PersonKind.Creator},
            {"Original Work Manager", PersonKind.Creator},
            {"Original Work Planning", PersonKind.Creator},
            {"Original Work Planning Assistance", PersonKind.Creator},
            {"Original Work Producer", PersonKind.Creator},
            {"Original Work Staff", PersonKind.Creator},
            {"Original Work Supervision", PersonKind.Creator},
            {"Original World Design", PersonKind.Creator},
            {"Overseas Assistance", PersonKind.Artist},
            {"Overseas Business", PersonKind.Artist},
            {"Overseas Coordination Manager", PersonKind.Producer},
            {"Overseas Coordinator", PersonKind.Producer},
            {"Overseas Distribution", PersonKind.Artist},
            {"Overseas Distribution Assistance", PersonKind.Artist},
            {"Overseas Distribution Generalisation", PersonKind.Artist},
            {"Overseas Licence", PersonKind.Artist},
            {"Overseas Licence Assistance", PersonKind.Artist},
            {"Overseas Manager", PersonKind.Producer},
            {"Overseas Marketing", PersonKind.Artist},
            {"Overseas Marketing Desk", PersonKind.Artist},
            {"Overseas Producer", PersonKind.Producer},
            {"Overseas Program Marketing", PersonKind.Artist},
            {"Overseas Promotion", PersonKind.Artist},
            {"Overseas Public Relations", PersonKind.Artist},
            {"Overseas Public Relations & Distribution", PersonKind.Artist},
            {"Overseas Publicity", PersonKind.Artist},
            {"Overseas Publicity Assistance", PersonKind.Artist},
            {"Overseas Publicity Generalisation", PersonKind.Artist},
            {"Overseas Recording Coordinator", PersonKind.Producer},
            {"Package Art", PersonKind.Artist},
            {"Package Coordinator", PersonKind.Producer},
            {"Package Design", PersonKind.Illustrator},
            {"Package Manager", PersonKind.Producer},
            {"Package Production", PersonKind.Producer},
            {"Package Sales", PersonKind.Artist},
            {"Package Work", PersonKind.Artist},
            {"Paint-on-Glass Animation", PersonKind.Artist},
            {"Painting", PersonKind.Artist},
            {"Painting Design", PersonKind.Illustrator},
            {"Pamphlet Editing", PersonKind.Editor},
            {"Pamphlet Editing Assistance", PersonKind.Editor},
            {"Pamphlet Work", PersonKind.Artist},
            {"Panel Painting", PersonKind.Artist},
            {"Papercraft", PersonKind.Artist},
            {"Pattern Switcher", PersonKind.Artist},
            {"Penguin Animation", PersonKind.Artist},
            {"Performance", PersonKind.Artist},
            {"Performance Assistance", PersonKind.Artist},
            {"Performance Direction", PersonKind.Director},
            {"Performance Scene Direction", PersonKind.Director},
            {"Performance Scene Storyboard", PersonKind.Director},
            {"Personality", PersonKind.Artist},
            {"Pharmaceutics Supervisor", PersonKind.Director},
            {"Photograph", PersonKind.Artist},
            {"Photograph Archive Assistance", PersonKind.Artist},
            {"Photograph Processing", PersonKind.Artist},
            {"Photographic Direction", PersonKind.Director},
            {"Photographic Direction Assistance", PersonKind.Director},
            {"Photographic Finishing", PersonKind.Artist},
            {"Photography", PersonKind.Artist},
            {"Photography Advisor", PersonKind.Artist},
            {"Photography Assistance", PersonKind.Artist},
            {"Photography Chief", PersonKind.Artist},
            {"Photography Design", PersonKind.Illustrator},
            {"Photography Design Assistance", PersonKind.Illustrator},
            {"Photography Effects Animation", PersonKind.Artist},
            {"Photography Effects Design", PersonKind.Illustrator},
            {"Photography Inspection", PersonKind.Artist},
            {"Photography Manager", PersonKind.Producer},
            {"Photography Preparation", PersonKind.Artist},
            {"Photography Preparation Assistance", PersonKind.Artist},
            {"Photography Producer", PersonKind.Producer},
            {"Photography Staff", PersonKind.Artist},
            {"Photography Studio", PersonKind.Artist},
            {"Photography Supervision", PersonKind.Director},
            {"Photography Team Chief", PersonKind.Artist},
            {"Photography Technical Adviser", PersonKind.Artist},
            {"Photography Technical Assistance", PersonKind.Artist},
            {"Photography Tools", PersonKind.Artist},
            {"Photography Work", PersonKind.Artist},
            {"Photography Work Assistance", PersonKind.Artist},
            {"Physical Effects", PersonKind.Artist},
            {"Piano", PersonKind.Artist},
            {"Pipeline", PersonKind.Artist},
            {"Pipeline Manager", PersonKind.Producer},
            {"Pipeline Supervisor", PersonKind.Director},
            {"Planning", PersonKind.Producer},
            {"Planning & Production", PersonKind.Producer},
            {"Planning Assistance", PersonKind.Producer},
            {"Planning Coordinator", PersonKind.Producer},
            {"Planning Generalisation", PersonKind.Producer},
            {"Planning Manager", PersonKind.Producer},
            {"Planning Producer", PersonKind.Producer},
            {"Planning Supervision", PersonKind.Director},
            {"Plug-in Assistance", PersonKind.Artist},
            {"Poem", PersonKind.Artist},
            {"Posing", PersonKind.Artist},
            {"Post Production", PersonKind.Producer},
            {"Post Production Coordinator", PersonKind.Producer},
            {"Post Production Desk", PersonKind.Producer},
            {"Post Production Manager", PersonKind.Producer},
            {"Post Production Producer", PersonKind.Producer},
            {"Post Production Sound Accountant", PersonKind.Producer},
            {"Post Production Sound Services", PersonKind.Producer},
            {"Post Production Supervision", PersonKind.Director},
            {"Poster Design", PersonKind.Illustrator},
            {"Poster Illustration", PersonKind.Illustrator},
            {"Poster Materials Assistance", PersonKind.Artist},
            {"Postrecording", PersonKind.Artist},
            {"Postrecording Assistance", PersonKind.Artist},
            {"Postrecording Assistant", PersonKind.Artist},
            {"Postrecording Assistant Direction", PersonKind.Director},
            {"Postrecording Direction", PersonKind.Director},
            {"Postrecording Mixer", PersonKind.Mixer},
            {"Postrecording Script", PersonKind.Writer},
            {"Postrecording Script Printing", PersonKind.Writer},
            {"Postrecording Studio", PersonKind.Artist},
            {"Postrecording Work", PersonKind.Artist},
            {"Pre-Production Assistance", PersonKind.Producer},
            {"Prescoring Assistance", PersonKind.Artist},
            {"Press Release Work", PersonKind.Artist},
            {"Preview", PersonKind.Artist},
            {"Preview Animation", PersonKind.Artist},
            {"Preview Assistance", PersonKind.Artist},
            {"Preview CG Animation", PersonKind.Artist},
            {"Preview CG Assistance", PersonKind.Artist},
            {"Preview Chibi Character", PersonKind.Artist},
            {"Preview Design", PersonKind.Illustrator},
            {"Preview Direction", PersonKind.Director},
            {"Preview Frame Work", PersonKind.Artist},
            {"Preview Illustration", PersonKind.Illustrator},
            {"Preview Illustration Assistance", PersonKind.Illustrator},
            {"Preview Manga", PersonKind.Artist},
            {"Preview Photography Assistance", PersonKind.Artist},
            {"Preview Script", PersonKind.Writer},
            {"Preview Video", PersonKind.Artist},
            {"Preview Work", PersonKind.Artist},
            {"Previs Artists", PersonKind.Artist},
            {"Previs Asset Creators", PersonKind.Artist},
            {"Previs Supervisor", PersonKind.Director},
            {"Previsualization", PersonKind.Artist},
            {"Producer", PersonKind.Producer},
            {"Producer Agent", PersonKind.Producer},
            {"Producer Desk", PersonKind.Producer},
            {"Product Advisor", PersonKind.Artist},
            {"Production & Publication", PersonKind.Producer},
            {"Production Accounting", PersonKind.Producer},
            {"Production Advisor", PersonKind.Producer},
            {"Production Assistance", PersonKind.Producer},
            {"Production Assistant", PersonKind.Producer},
            {"Production Associate", PersonKind.Producer},
            {"Production Chief", PersonKind.Producer},
            {"Production Committee", PersonKind.Producer},
            {"Production Committee Chief", PersonKind.Producer},
            {"Production Committee Desk", PersonKind.Producer},
            {"Production Committee Support", PersonKind.Producer},
            {"Production Coordinator", PersonKind.Producer},
            {"Production Design", PersonKind.Illustrator},
            {"Production Design Assistant", PersonKind.Illustrator},
            {"Production Desk", PersonKind.Producer},
            {"Production Desk Assistant", PersonKind.Producer},
            {"Production Director", PersonKind.Director},
            {"Production Driver", PersonKind.Producer},
            {"Production Engineer", PersonKind.Engineer},
            {"Production Generalisation", PersonKind.Producer},
            {"Production Generalisation Assistance", PersonKind.Producer},
            {"Production Generalisation Associate", PersonKind.Producer},
            {"Production Manager", PersonKind.Producer},
            {"Production Office Work", PersonKind.Producer},
            {"Production Office Work Desk", PersonKind.Producer},
            {"Production Office Work Generalisation", PersonKind.Producer},
            {"Production Office Work Manager", PersonKind.Producer},
            {"Production Organisation", PersonKind.Producer},
            {"Production Producer", PersonKind.Producer},
            {"Production Sales", PersonKind.Producer},
            {"Production Secretary", PersonKind.Producer},
            {"Production Supervision", PersonKind.Director},
            {"Production Support", PersonKind.Producer},
            {"Programme Advisor", PersonKind.Artist},
            {"Programme Development", PersonKind.Artist},
            {"Programme Development Assistance", PersonKind.Artist},
            {"Programme Producer", PersonKind.Producer},
            {"Programming Assistance", PersonKind.Artist},
            {"Project Coordinator", PersonKind.Producer},
            {"Project Generalisation", PersonKind.Artist},
            {"Project Manager", PersonKind.Producer},
            {"Project Support", PersonKind.Artist},
            {"Promotion", PersonKind.Artist},
            {"Promotion Art Director", PersonKind.Director},
            {"Promotion Assistance", PersonKind.Artist},
            {"Promotion Coordinator", PersonKind.Producer},
            {"Promotion Design", PersonKind.Illustrator},
            {"Promotion Director", PersonKind.Director},
            {"Promotion Generalisation", PersonKind.Artist},
            {"Promotion Manager", PersonKind.Producer},
            {"Promotion Producer", PersonKind.Producer},
            {"Promotion Supervisor", PersonKind.Director},
            {"Promotional Video Work", PersonKind.Artist},
            {"Prop Animation Direction", PersonKind.Director},
            {"Prop Animation Direction Assistance", PersonKind.Director},
            {"Prop Design", PersonKind.Illustrator},
            {"Prop Design Assistance", PersonKind.Illustrator},
            {"Prop Material Creation", PersonKind.Artist},
            {"Prop Modelling", PersonKind.Artist},
            {"Prop Supervision", PersonKind.Director},
            {"Property Design", PersonKind.Illustrator},
            {"Prototyping", PersonKind.Artist},
            {"Prototyping Assistance", PersonKind.Artist},
            {"Public Relations", PersonKind.Artist},
            {"Public Relations Assistance", PersonKind.Artist},
            {"Public Relations Manager", PersonKind.Producer},
            {"Public Relations Office", PersonKind.Artist},
            {"Public Relations Producer", PersonKind.Producer},
            {"Public Relations Publicity", PersonKind.Artist},
            {"Publication", PersonKind.Artist},
            {"Publication Assistance", PersonKind.Artist},
            {"Publication Manager", PersonKind.Producer},
            {"Publication Producer", PersonKind.Producer},
            {"Publicity", PersonKind.Artist},
            {"Publicity Activity", PersonKind.Artist},
            {"Publicity Art", PersonKind.Artist},
            {"Publicity Art Direction", PersonKind.Director},
            {"Publicity Assistance", PersonKind.Artist},
            {"Publicity Assistant Producer", PersonKind.Producer},
            {"Publicity Associate", PersonKind.Artist},
            {"Publicity Chief", PersonKind.Artist},
            {"Publicity Copy", PersonKind.Artist},
            {"Publicity Creative", PersonKind.Artist},
            {"Publicity Design", PersonKind.Illustrator},
            {"Publicity Design Assistance", PersonKind.Illustrator},
            {"Publicity Direction", PersonKind.Director},
            {"Publicity Executive Producer", PersonKind.Producer},
            {"Publicity Generalisation", PersonKind.Artist},
            {"Publicity Literature", PersonKind.Artist},
            {"Publicity Literature Assistance", PersonKind.Artist},
            {"Publicity Manager", PersonKind.Producer},
            {"Publicity Material Work", PersonKind.Artist},
            {"Publicity Package Design", PersonKind.Illustrator},
            {"Publicity Planning", PersonKind.Producer},
            {"Publicity Planning Assistance", PersonKind.Producer},
            {"Publicity Producer", PersonKind.Producer},
            {"Publicity Still Photography", PersonKind.Artist},
            {"Publicity Supervisor", PersonKind.Director},
            {"Publicity Tie-up", PersonKind.Artist},
            {"Publicity Writer", PersonKind.Artist},
            {"Publishing Partners", PersonKind.Artist},
            {"Puppet Animation", PersonKind.Artist},
            {"Puppet Design", PersonKind.Illustrator},
            {"Quality Control Assistance", PersonKind.Artist},
            {"Quattro", PersonKind.Artist},
            {"Re-recording", PersonKind.Artist},
            {"Re-recording Mixer", PersonKind.Mixer},
            {"Re-recording Mixer Assistant", PersonKind.Mixer},
            {"Real Time Engineer", PersonKind.Engineer},
            {"Real Time Recording", PersonKind.Artist},
            {"Recommendation", PersonKind.Artist},
            {"Record", PersonKind.Artist},
            {"Recording", PersonKind.Artist},
            {"Recording & Mixing Engineer", PersonKind.Mixer},
            {"Recording Adjustment", PersonKind.Artist},
            {"Recording Adjustment Assistant", PersonKind.Artist},
            {"Recording Adjustment Engineer", PersonKind.Engineer},
            {"Recording Adjustment Studio", PersonKind.Artist},
            {"Recording Advisor", PersonKind.Artist},
            {"Recording Assistance", PersonKind.Artist},
            {"Recording Assistant", PersonKind.Artist},
            {"Recording Coordinator", PersonKind.Producer},
            {"Recording Design", PersonKind.Illustrator},
            {"Recording Direction", PersonKind.Director},
            {"Recording Direction Assistant", PersonKind.Director},
            {"Recording Engineer", PersonKind.Engineer},
            {"Recording Engineer Assistant", PersonKind.Engineer},
            {"Recording Operator", PersonKind.Artist},
            {"Recording Producer", PersonKind.Producer},
            {"Recording Production", PersonKind.Producer},
            {"Recording Studio", PersonKind.Artist},
            {"Recording Studio Assistance", PersonKind.Artist},
            {"Recording Studio Equipment Assistance", PersonKind.Artist},
            {"Recording Studio Work", PersonKind.Artist},
            {"Recording Supervision", PersonKind.Director},
            {"Recording Work", PersonKind.Artist},
            {"Recording Work Assistance", PersonKind.Artist},
            {"Recording Work Desk", PersonKind.Artist},
            {"Recording Work Manager", PersonKind.Producer},
            {"Reference Dancer", PersonKind.Artist},
            {"Reference Data", PersonKind.Artist},
            {"Reference Photograph", PersonKind.Artist},
            {"Reference Photography", PersonKind.Artist},
            {"Regional Assistance", PersonKind.Artist},
            {"Rendering", PersonKind.Artist},
            {"Rendering Assistance", PersonKind.Artist},
            {"Rendering Chief", PersonKind.Artist},
            {"Research", PersonKind.Artist},
            {"Research & Development", PersonKind.Artist},
            {"Research & Development Supervisor", PersonKind.Director},
            {"Research Assistance", PersonKind.Artist},
            {"Retake Manager", PersonKind.Producer},
            {"Retouch", PersonKind.Artist},
            {"Rigging", PersonKind.Artist},
            {"Rigging & Simulation", PersonKind.Artist},
            {"Rigging & Simulation Supervisor", PersonKind.Director},
            {"Rigging Assistance", PersonKind.Artist},
            {"Rigging Chief", PersonKind.Artist},
            {"Rigging Consultant", PersonKind.Artist},
            {"Rigging Director", PersonKind.Director},
            {"Rigging Lead", PersonKind.Artist},
            {"Rigging Supervisor", PersonKind.Director},
            {"Rigging Tool Development", PersonKind.Artist},
            {"Robot Design", PersonKind.Illustrator},
            {"Rotoscope", PersonKind.Artist},
            {"Rotoscope Assistance", PersonKind.Artist},
            {"Russian Translation Assistance", PersonKind.Translator},
            {"SD Character Animation", PersonKind.Artist},
            {"SD Character Design", PersonKind.Illustrator},
            {"SD Character Work", PersonKind.Artist},
            {"SDGs Supervision", PersonKind.Director},
            {"SF Research", PersonKind.Artist},
            {"SUBTITLE-PROGRAMMING", PersonKind.Artist},
            {"Sales Agency", PersonKind.Artist},
            {"Sales Generalisation", PersonKind.Artist},
            {"Sales Manager", PersonKind.Producer},
            {"Scan", PersonKind.Artist},
            {"Scan Chief", PersonKind.Artist},
            {"Scan Correction", PersonKind.Artist},
            {"Scat", PersonKind.Artist},
            {"Scenario Advisor", PersonKind.Writer},
            {"Scenario Assistance", PersonKind.Writer},
            {"Scenario Coordinator", PersonKind.Writer},
            {"Scenario Supervision", PersonKind.Director},
            {"Scenario Work", PersonKind.Writer},
            {"Sci-Fi Setting", PersonKind.Artist},
            {"Sci-Fi Setting Assistance", PersonKind.Artist},
            {"Science Supervision", PersonKind.Director},
            {"Score Copyist", PersonKind.Composer},
            {"Score Mixer", PersonKind.Composer},
            {"Score Support", PersonKind.Composer},
            {"Screen Design", PersonKind.Illustrator},
            {"Screen Design Direction", PersonKind.Director},
            {"Screen Supervision", PersonKind.Director},
            {"Script Development", PersonKind.Writer},
            {"Script Production Assistance", PersonKind.Writer},
            {"Script Work", PersonKind.Writer},
            {"Script/Screenplay", PersonKind.Writer},
            {"Script/Screenplay Assistance", PersonKind.Writer},
            {"Script/Screenplay Proofreading", PersonKind.Writer},
            {"Senior Animation Producer", PersonKind.Producer},
            {"Senior Animator", PersonKind.Artist},
            {"Senior CG Director", PersonKind.Director},
            {"Senior Producer", PersonKind.Producer},
            {"Sequence Supervisor", PersonKind.Director},
            {"Serialisation", PersonKind.Artist},
            {"Serialisation Assistance", PersonKind.Artist},
            {"Serialisation Manager", PersonKind.Producer},
            {"Series Composition Advisor", PersonKind.Artist},
            {"Series Composition Assistant", PersonKind.Artist},
            {"Series Composition Supervision", PersonKind.Artist},
            {"Series Design", PersonKind.Illustrator},
            {"Series Production Direction", PersonKind.Director},
            {"Set Design", PersonKind.Illustrator},
            {"Set Design Assistant", PersonKind.Illustrator},
            {"Set Design Producer", PersonKind.Producer},
            {"Sets & Props", PersonKind.Artist},
            {"Sets & Props Supervisor", PersonKind.Director},
            {"Setting", PersonKind.Artist},
            {"Setting Supervisor", PersonKind.Director},
            {"Setup", PersonKind.Artist},
            {"Setup Assistance", PersonKind.Artist},
            {"Setup Director", PersonKind.Director},
            {"Setup Lead", PersonKind.Artist},
            {"Setup Work", PersonKind.Artist},
            {"Shading Chief", PersonKind.Artist},
            {"Shamisen", PersonKind.Artist},
            {"Simulation Artist", PersonKind.Artist},
            {"Simulation Supervisor", PersonKind.Director},
            {"Single", PersonKind.Artist},
            {"Sketch", PersonKind.Artist},
            {"Skill Design", PersonKind.Illustrator},
            {"Software", PersonKind.Artist},
            {"Software Assistance", PersonKind.Artist},
            {"Software Development", PersonKind.Artist},
            {"Some Original Character Design", PersonKind.Illustrator},
            {"Sound", PersonKind.Engineer},
            {"Sound Adjustment", PersonKind.Engineer},
            {"Sound Adjustment Assistant", PersonKind.Engineer},
            {"Sound Adjustment Manager", PersonKind.Producer},
            {"Sound Adjustment Supervision", PersonKind.Director},
            {"Sound Advisor", PersonKind.Engineer},
            {"Sound Architect", PersonKind.Engineer},
            {"Sound Assistance", PersonKind.Engineer},
            {"Sound Concept", PersonKind.Engineer},
            {"Sound Coordinator", PersonKind.Producer},
            {"Sound Design", PersonKind.Engineer},
            {"Sound Design Assistant", PersonKind.Engineer},
            {"Sound Design Supervision", PersonKind.Director},
            {"Sound Desk", PersonKind.Engineer},
            {"Sound Direction", PersonKind.Director},
            {"Sound Direction Assistant", PersonKind.Director},
            {"Sound Editing", PersonKind.Engineer},
            {"Sound Editor", PersonKind.Engineer},
            {"Sound Effects", PersonKind.Engineer},
            {"Sound Effects Assistance", PersonKind.Engineer},
            {"Sound Effects Assistant", PersonKind.Engineer},
            {"Sound Effects Design", PersonKind.Engineer},
            {"Sound Effects Direction", PersonKind.Director},
            {"Sound Effects Editor", PersonKind.Engineer},
            {"Sound Effects Recording", PersonKind.Engineer},
            {"Sound Effects Recording Assistance", PersonKind.Engineer},
            {"Sound Effects Work", PersonKind.Engineer},
            {"Sound Engineer", PersonKind.Engineer},
            {"Sound Management", PersonKind.Producer},
            {"Sound Manipulation", PersonKind.Engineer},
            {"Sound Material Assistance", PersonKind.Engineer},
            {"Sound Mixing", PersonKind.Mixer},
            {"Sound Mixing Advisor", PersonKind.Mixer},
            {"Sound Mixing Assistant", PersonKind.Mixer},
            {"Sound Mixing Engineer", PersonKind.Mixer},
            {"Sound Mixing Studio", PersonKind.Mixer},
            {"Sound Mixing Technician", PersonKind.Mixer},
            {"Sound Office Work", PersonKind.Engineer},
            {"Sound Pre-Mixing Mixer", PersonKind.Mixer},
            {"Sound Producer", PersonKind.Producer},
            {"Sound Production", PersonKind.Producer},
            {"Sound Production Assistance", PersonKind.Producer},
            {"Sound Programming", PersonKind.Engineer},
            {"Sound Recording", PersonKind.Engineer},
            {"Sound Studio", PersonKind.Engineer},
            {"Sound Supervision", PersonKind.Director},
            {"Sound Technical Assistance", PersonKind.Engineer},
            {"Sound Work", PersonKind.Engineer},
            {"Sound Work Assistance", PersonKind.Engineer},
            {"Sound Work Desk", PersonKind.Engineer},
            {"Sound Work Manager", PersonKind.Producer},
            {"Sound Work Producer", PersonKind.Producer},
            {"Soundtrack", PersonKind.Composer},
            {"Soundtrack Director", PersonKind.Composer},
            {"Soundtrack Music Producer", PersonKind.Composer},
            {"Soundtrack Music Work", PersonKind.Composer},
            {"Soundtrack Sales", PersonKind.Composer},
            {"Soundtrack Work Assistance", PersonKind.Composer},
            {"Soundtrack Work Director", PersonKind.Composer},
            {"Source", PersonKind.Artist},
            {"Special 3D CGI Animator", PersonKind.Artist},
            {"Special Advisor", PersonKind.Artist},
            {"Special Animation", PersonKind.Artist},
            {"Special Animation Photography", PersonKind.Artist},
            {"Special Animator", PersonKind.Artist},
            {"Special Art Design", PersonKind.Illustrator},
            {"Special Assistance", PersonKind.Artist},
            {"Special Character Design", PersonKind.Illustrator},
            {"Special Clothing Design", PersonKind.Illustrator},
            {"Special Conceptor", PersonKind.Artist},
            {"Special Design", PersonKind.Illustrator},
            {"Special Design Assistance", PersonKind.Illustrator},
            {"Special Digital Photography", PersonKind.Artist},
            {"Special Editing", PersonKind.Editor},
            {"Special Effects", PersonKind.Artist},
            {"Special Effects Animation Assistance", PersonKind.Artist},
            {"Special Effects Animation Supervision", PersonKind.Director},
            {"Special Effects Assistance", PersonKind.Artist},
            {"Special Effects Coordinator", PersonKind.Producer},
            {"Special Effects Direction", PersonKind.Director},
            {"Special Effects Direction Assistant", PersonKind.Director},
            {"Special Effects Lead", PersonKind.Artist},
            {"Special Effects Processing", PersonKind.Artist},
            {"Special Effects Producer", PersonKind.Producer},
            {"Special Effects Set", PersonKind.Artist},
            {"Special Effects Supervision", PersonKind.Director},
            {"Special Image Processing", PersonKind.Artist},
            {"Special Mechanical Design", PersonKind.Illustrator},
            {"Special Performance", PersonKind.Artist},
            {"Special Photography", PersonKind.Artist},
            {"Special Script/Screenplay", PersonKind.Writer},
            {"Special Set Design", PersonKind.Illustrator},
            {"Special Skill", PersonKind.Artist},
            {"Special Skill Direction", PersonKind.Director},
            {"Special Skill Effects", PersonKind.Artist},
            {"Special Skill Supervision", PersonKind.Director},
            {"Special Sound Design", PersonKind.Engineer},
            {"Special Support", PersonKind.Artist},
            {"Special Thanks", PersonKind.Artist},
            {"Special Work Assistance", PersonKind.Artist},
            {"Sponsor", PersonKind.Artist},
            {"Sponsor Screen Illustration", PersonKind.Illustrator},
            {"Sports Supervision", PersonKind.Director},
            {"Staff Roll", PersonKind.Artist},
            {"Stage Design", PersonKind.Illustrator},
            {"Stage Design Assistance", PersonKind.Illustrator},
            {"Stereo CG Work", PersonKind.Artist},
            {"Stereo Compositor", PersonKind.Artist},
            {"Stereo Mixing Engineer", PersonKind.Mixer},
            {"Stereo Production Manager", PersonKind.Producer},
            {"Stereoscopic 3D Artist", PersonKind.Artist},
            {"Stereoscopic 3D Direction", PersonKind.Director},
            {"Stereoscopic 3D Editing", PersonKind.Editor},
            {"Stereoscopic Assistance", PersonKind.Artist},
            {"Stereoscopic Director", PersonKind.Director},
            {"Stereoscopic Supervisor", PersonKind.Director},
            {"Stereotype", PersonKind.Artist},
            {"Still Assistance", PersonKind.Artist},
            {"Still Materials", PersonKind.Artist},
            {"Still Recording", PersonKind.Artist},
            {"Stop Motion Animation", PersonKind.Artist},
            {"Story Advisor", PersonKind.Writer},
            {"Story Composition", PersonKind.Artist},
            {"Story Concept", PersonKind.Writer},
            {"Story Director", PersonKind.Director},
            {"Story Editor", PersonKind.Writer},
            {"Storyboard", PersonKind.Director},
            {"Storyboard Assistant", PersonKind.Director},
            {"Storyboard Check", PersonKind.Director},
            {"Storyboard Chief", PersonKind.Director},
            {"Storyboard Clean-up", PersonKind.Director},
            {"Storyboard Copy", PersonKind.Director},
            {"Storyboard Direction", PersonKind.Director},
            {"Storyboard Manager", PersonKind.Director},
            {"Storyboard Supervision", PersonKind.Director},
            {"Studio Assistant", PersonKind.Artist},
            {"Studio Coordinator", PersonKind.Producer},
            {"Studio Director", PersonKind.Director},
            {"Studio Head", PersonKind.Artist},
            {"Studio Management", PersonKind.Producer},
            {"Studio Operator", PersonKind.Artist},
            {"Studio Staff", PersonKind.Artist},
            {"Stunt Coordination", PersonKind.Artist},
            {"Stunts", PersonKind.Artist},
            {"Stylist", PersonKind.Artist},
            {"Sub 3DCG", PersonKind.Artist},
            {"Sub Character Animation Direction", PersonKind.Director},
            {"Sub Character Design", PersonKind.Illustrator},
            {"Sub Character Design Assistance", PersonKind.Illustrator},
            {"Sub Clothing Design", PersonKind.Illustrator},
            {"Sub Composite Lead", PersonKind.Artist},
            {"Sub Design Assistance", PersonKind.Illustrator},
            {"Sub Mechanical Design", PersonKind.Illustrator},
            {"Sub-Lead", PersonKind.Artist},
            {"Subtitle Assistance", PersonKind.Translator},
            {"Subtitle Design", PersonKind.Translator},
            {"Subtitle Font", PersonKind.Translator},
            {"Subtitle Illustration", PersonKind.Translator},
            {"Subtitle Work", PersonKind.Translator},
            {"Supervising Producer", PersonKind.Producer},
            {"Supervising Sound Editor", PersonKind.Engineer},
            {"Supervision", PersonKind.Director},
            {"Supervision Assistance", PersonKind.Director},
            {"Supplementary Sound Channel", PersonKind.Engineer},
            {"Support Engineer", PersonKind.Engineer},
            {"Surround Mastering Engineer", PersonKind.Engineer},
            {"Surround Mastering Studio", PersonKind.Artist},
            {"Surround Mixing Assistant Engineer", PersonKind.Mixer},
            {"Surround Mixing Engineer", PersonKind.Mixer},
            {"Surround Mixing Studio", PersonKind.Mixer},
            {"Surround Music Mixing Engineer", PersonKind.Mixer},
            {"Surround Studio", PersonKind.Artist},
            {"System Coordinator", PersonKind.Producer},
            {"System Design", PersonKind.Illustrator},
            {"System Engineer", PersonKind.Engineer},
            {"System Management Assistance", PersonKind.Producer},
            {"System Manager", PersonKind.Producer},
            {"System Solution", PersonKind.Artist},
            {"System Support", PersonKind.Artist},
            {"TD Studio", PersonKind.Artist},
            {"TV Programme Manager", PersonKind.Producer},
            {"TV Relationship", PersonKind.Artist},
            {"Talent Generalisation", PersonKind.Artist},
            {"Technical Advisor", PersonKind.Artist},
            {"Technical Artist", PersonKind.Artist},
            {"Technical Assistance", PersonKind.Artist},
            {"Technical Assistant Director", PersonKind.Director},
            {"Technical Coordinator", PersonKind.Producer},
            {"Technical Design", PersonKind.Illustrator},
            {"Technical Designer", PersonKind.Illustrator},
            {"Technical Director", PersonKind.Director},
            {"Technical Effects", PersonKind.Artist},
            {"Technical Engineer", PersonKind.Engineer},
            {"Technical Producer", PersonKind.Producer},
            {"Technical Supervision", PersonKind.Director},
            {"Technical Support", PersonKind.Artist},
            {"Technique", PersonKind.Artist},
            {"Telecine", PersonKind.Artist},
            {"Telop", PersonKind.Artist},
            {"Telop Animation", PersonKind.Artist},
            {"Telop Design", PersonKind.Illustrator},
            {"Telop Editing", PersonKind.Editor},
            {"Telop Materials", PersonKind.Artist},
            {"Textile Conversion", PersonKind.Artist},
            {"Textile Design", PersonKind.Illustrator},
            {"Textile Design Manager", PersonKind.Producer},
            {"Texture", PersonKind.Artist},
            {"Texture Artist", PersonKind.Artist},
            {"Texture Assistance", PersonKind.Artist},
            {"Texture Design", PersonKind.Illustrator},
            {"Texture Lead", PersonKind.Artist},
            {"Texture Paint", PersonKind.Artist},
            {"Thanks", PersonKind.Artist},
            {"Theatre Authoring Engineer", PersonKind.Engineer},
            {"Theatre Manager", PersonKind.Producer},
            {"Theatre Pamphlet", PersonKind.Artist},
            {"Theatre Pamphlet Work", PersonKind.Artist},
            {"Theatre Sales", PersonKind.Artist},
            {"Theatre Sales Assistance", PersonKind.Artist},
            {"Theme Song", PersonKind.Artist},
            {"Theme Song Assistance", PersonKind.Artist},
            {"Theme Song Assistant Producer", PersonKind.Producer},
            {"Theme Song Dance Choreography", PersonKind.Artist},
            {"Theme Song Dance Supervision", PersonKind.Director},
            {"Theme Song Director", PersonKind.Director},
            {"Theme Song Producer", PersonKind.Producer},
            {"Theme Song Production Manager", PersonKind.Producer},
            {"Theme Song Promotion", PersonKind.Artist},
            {"Theme Song Recording", PersonKind.Artist},
            {"Tie-Up", PersonKind.Artist},
            {"Tie-Up Coordinator", PersonKind.Producer},
            {"Tie-up Assistance", PersonKind.Artist},
            {"Tie-up Designer", PersonKind.Illustrator},
            {"Tie-up Manager", PersonKind.Producer},
            {"Timing", PersonKind.Artist},
            {"Title", PersonKind.Artist},
            {"Title & Lith Work", PersonKind.Artist},
            {"Title Animation", PersonKind.Artist},
            {"Title Background Art", PersonKind.Artist},
            {"Title CG", PersonKind.Artist},
            {"Title Cut", PersonKind.Artist},
            {"Title Design", PersonKind.Illustrator},
            {"Title Direction", PersonKind.Director},
            {"Title Layout", PersonKind.Artist},
            {"Title Logo", PersonKind.Artist},
            {"Title Logo Assistance", PersonKind.Artist},
            {"Title Photography", PersonKind.Artist},
            {"Title Processing", PersonKind.Artist},
            {"Title Supervision", PersonKind.Director},
            {"Title Telop", PersonKind.Artist},
            {"Title Work", PersonKind.Artist},
            {"Trace Machine", PersonKind.Artist},
            {"Tracing", PersonKind.Artist},
            {"Tracing & Painting Correction", PersonKind.Artist},
            {"Tracing Chief", PersonKind.Artist},
            {"Trailer", PersonKind.Artist},
            {"Trailer Assistance", PersonKind.Artist},
            {"Trailer CG", PersonKind.Artist},
            {"Trailer Director", PersonKind.Director},
            {"Trailer Manager", PersonKind.Producer},
            {"Trailer Producer", PersonKind.Producer},
            {"Trailer Work", PersonKind.Artist},
            {"Trailer Work Manager", PersonKind.Producer},
            {"Translation", PersonKind.Translator},
            {"Translation Assistance", PersonKind.Translator},
            {"Translation Inspection", PersonKind.Translator},
            {"Translation Inspection Assistance", PersonKind.Translator},
            {"Twelve-string guitar", PersonKind.Artist},
            {"Typography", PersonKind.Artist},
            {"Unit CG Direction", PersonKind.Director},
            {"Unit CG Producer", PersonKind.Producer},
            {"Unity Engineer", PersonKind.Engineer},
            {"VFX Telop Motion", PersonKind.Artist},
            {"Vehicle Assistance", PersonKind.Artist},
            {"Vehicle Manager", PersonKind.Producer},
            {"Video Coordinator", PersonKind.Producer},
            {"Video Director", PersonKind.Director},
            {"Video Promotion", PersonKind.Artist},
            {"Video Publishing", PersonKind.Artist},
            {"Video Services", PersonKind.Artist},
            {"Video Work", PersonKind.Artist},
            {"Video Work Assistance", PersonKind.Artist},
            {"Video Work Producer", PersonKind.Producer},
            {"Videogram", PersonKind.Artist},
            {"Videogram Licence", PersonKind.Artist},
            {"Videogram Publicity", PersonKind.Artist},
            {"Videotape", PersonKind.Artist},
            {"Visual Advisor", PersonKind.Artist},
            {"Visual Art", PersonKind.Artist},
            {"Visual Composer", PersonKind.Composer},
            {"Visual Concept", PersonKind.Artist},
            {"Visual Conductor", PersonKind.Conductor},
            {"Visual Coordinator", PersonKind.Producer},
            {"Visual Design", PersonKind.Illustrator},
            {"Visual Direction", PersonKind.Director},
            {"Visual Effects", PersonKind.Artist},
            {"Visual Effects Assistance", PersonKind.Artist},
            {"Visual Processing", PersonKind.Artist},
            {"Visual Works", PersonKind.Artist},
            {"Vocal Recording", PersonKind.Artist},
            {"Vocals", PersonKind.Artist},
            {"Vocals/Performed by", PersonKind.Artist},
            {"Voice", PersonKind.Actor},
            {"Voice Direction", PersonKind.Director},
            {"Watercolour Finishing", PersonKind.Colorist},
            {"Weapon Animation Direction", PersonKind.Director},
            {"Weapon Animation Direction Assistance", PersonKind.Director},
            {"Weapon Colouring", PersonKind.Colorist},
            {"Weapon Design", PersonKind.Illustrator},
            {"Weapon Design Assistance", PersonKind.Illustrator},
            {"Weapon Effects Supervision", PersonKind.Director},
            {"Weapon Modelling", PersonKind.Artist},
            {"Weapon Special Effects", PersonKind.Artist},
            {"Weapon Supervision", PersonKind.Director},
            {"Weapon Supervision Assistance", PersonKind.Director},
            {"Web Animation", PersonKind.Artist},
            {"Web Assistance", PersonKind.Artist},
            {"Web Design", PersonKind.Illustrator},
            {"Web Illustration", PersonKind.Illustrator},
            {"Web Manager", PersonKind.Producer},
            {"Web Producer", PersonKind.Producer},
            {"Web Promotion", PersonKind.Artist},
            {"Web Promotion Assistance", PersonKind.Artist},
            {"Web Publicity", PersonKind.Artist},
            {"Web Radio", PersonKind.Artist},
            {"Web Supervisor", PersonKind.Director},
            {"Web Writer", PersonKind.Artist},
            {"Work Advisor", PersonKind.Artist},
            {"Work Assistance", PersonKind.Artist},
            {"Work Assistant", PersonKind.Artist},
            {"Work Direction", PersonKind.Director},
            {"Work Management", PersonKind.Producer},
            {"Work Staff", PersonKind.Artist},
            {"Work Studio", PersonKind.Artist},
            {"Work Supervision", PersonKind.Director},
            {"World Design", PersonKind.Illustrator},
            {"World Supervision", PersonKind.Director},
            {"World Visual Designer", PersonKind.Illustrator},
            {"Xerography", PersonKind.Artist},
        };

        public AniDbSeriesProvider(IApplicationPaths appPaths)
        {
            _appPaths = appPaths;
            TitleMatcher = AniDbTitleMatcher.DefaultInstance;
            Current = this;
        }

        private static AniDbSeriesProvider Current { get; set; }
        private IAniDbTitleMatcher TitleMatcher { get; set; }
        public int Order => -1;
        public string Name => "AniDB";

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var animeId = info.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            if (string.IsNullOrEmpty(animeId) && !string.IsNullOrEmpty(info.Name))
            {
                animeId = await Equals_check.XmlFindId(info.Name, cancellationToken);
            }

            if (!string.IsNullOrEmpty(animeId))
            {
                return await GetMetadataForId(animeId, info, cancellationToken);
            }

            return new MetadataResult<Series>();
        }

        public async Task<MetadataResult<Series>> GetMetadataForId(string animeId, SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();

            result.Item = new Series();
            result.HasMetadata = true;

            result.Item.ProviderIds.Add(ProviderNames.AniDb, animeId);

            var seriesDataPath = await GetSeriesData(_appPaths, animeId, cancellationToken);
            await FetchSeriesInfo(result, seriesDataPath, info.MetadataLanguage ?? "en").ConfigureAwait(false);

            return result;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            var animeId = searchInfo.ProviderIds.GetOrDefault(ProviderNames.AniDb);

            if (!string.IsNullOrEmpty(animeId))
            {
                var resultMetadata = await GetMetadataForId(animeId, searchInfo, cancellationToken);

                if (resultMetadata.HasMetadata)
                {
                    var imageProvider = new AniDbImageProvider(_appPaths);
                    var images = await imageProvider.GetImages(animeId, cancellationToken);
                    results.Add(MetadataToRemoteSearchResult(resultMetadata, images));
                }
            }

            if (!string.IsNullOrEmpty(searchInfo.Name))
            {
                List<RemoteSearchResult> name_results = await GetSearchResultsByName(searchInfo.Name, searchInfo, cancellationToken).ConfigureAwait(false);

                foreach (var media in name_results)
                {
                    results.Add(media);
                }
            }

            return results;
        }

        public async Task<List<RemoteSearchResult>> GetSearchResultsByName(string name, SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            var imageProvider = new AniDbImageProvider(_appPaths);
            var results = new List<RemoteSearchResult>();

            List<string> ids = await Equals_check.XmlSearch(name, cancellationToken);

            foreach (string id in ids)
            {
                var resultMetadata = await GetMetadataForId(id, searchInfo, cancellationToken);

                if (resultMetadata.HasMetadata)
                {
                    var images = await imageProvider.GetImages(id, cancellationToken);
                    results.Add(MetadataToRemoteSearchResult(resultMetadata, images));
                }
            }
            return results;
        }

        public RemoteSearchResult MetadataToRemoteSearchResult(MetadataResult<Series> metadata, IEnumerable<RemoteImageInfo> images)
        {
            return new RemoteSearchResult
            {
                Name = metadata.Item.Name,
                ProductionYear = metadata.Item.PremiereDate?.Year,
                PremiereDate = metadata.Item.PremiereDate,
                ImageUrl = images.Any() ? images.First().Url : null,
                ProviderIds = metadata.Item.ProviderIds,
                SearchProviderName = ProviderNames.AniDb
            };
        }

        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var httpClient = Plugin.Instance.GetHttpClient();

            return await httpClient.GetAsync(url).ConfigureAwait(false);
        }

        public static async Task<string> GetSeriesData(IApplicationPaths appPaths, string seriesId, CancellationToken cancellationToken)
        {
            var dataPath = GetSeriesDataPath(appPaths, seriesId);
            var seriesDataPath = Path.Combine(dataPath, SeriesDataFile);
            var fileInfo = new FileInfo(seriesDataPath);

            var isEmpty = fileInfo.Exists && fileInfo.Length == 0;
            var isStale = fileInfo.Exists && DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(Plugin.Instance.Configuration.MaxCacheAge);

            if (!fileInfo.Exists || isEmpty || isStale)
            {
                await DownloadSeriesData(seriesId, seriesDataPath, appPaths.CachePath, cancellationToken).ConfigureAwait(false);
            }

            return seriesDataPath;
        }

        private async Task FetchSeriesInfo(MetadataResult<Series> result, string seriesDataPath, string preferredMetadataLangauge)
        {
            var series = result.Item;
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = File.Open(seriesDataPath, FileMode.Open, FileAccess.Read))
            using (var reader = XmlReader.Create(streamReader, settings))
            {
                await reader.MoveToContentAsync().ConfigureAwait(false);

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "startdate":
                                var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                                if (!string.IsNullOrWhiteSpace(val))
                                {
                                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime date))
                                    {
                                        date = date.ToUniversalTime();
                                        series.PremiereDate = date;
                                    }
                                }

                                break;

                            case "enddate":
                                var endDate = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                                if (!string.IsNullOrWhiteSpace(endDate))
                                {
                                    if (DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime date))
                                    {
                                        date = date.ToUniversalTime();
                                        series.EndDate = date;
                                    }
                                }

                                break;

                            case "titles":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    var (title, originalTitle) = await ParseTitle(subtree, preferredMetadataLangauge).ConfigureAwait(false);
                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        series.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                                            ? title.Replace('`', '\'')
                                            : title;
                                    }
                                    if (!string.IsNullOrEmpty(originalTitle))
                                    {
                                        series.OriginalTitle = Plugin.Instance.Configuration.AniDbReplaceGraves
                                            ? originalTitle.Replace('`', '\'')
                                            : originalTitle;
                                    }
                                }

                                break;

                            case "creators":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    await ParseCreators(result, subtree).ConfigureAwait(false);
                                }

                                break;

                            case "description":
                                var description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                description = description.TrimStart('*').Trim();
                                series.Overview = ReplaceNewLine(StripAniDbLinks(
                                    Plugin.Instance.Configuration.AniDbReplaceGraves ? description.Replace('`', '\'') : description));

                                break;

                            case "ratings":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    ParseRatings(series, subtree);
                                }

                                break;

                            case "resources":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    await ParseResources(series, subtree).ConfigureAwait(false);
                                }

                                break;

                            case "characters":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    await ParseActors(result, subtree).ConfigureAwait(false);
                                }

                                break;

                            case "tags":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    await ParseTags(series, subtree).ConfigureAwait(false);
                                }

                                break;

                            case "episodes":
                                using (var subtree = reader.ReadSubtree())
                                {
                                    await ParseEpisodes(series, subtree).ConfigureAwait(false);
                                }

                                break;
                        }
                    }
                }
            }

            GenreHelper.CleanupGenres(series);
        }

        private async Task ParseEpisodes(Series series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "episode")
                {
                    if (int.TryParse(reader.GetAttribute("id"), out int id) && IgnoredTagIds.Contains(id))
                    {
                        continue;
                    }

                    using (var episodeSubtree = reader.ReadSubtree())
                    {
                        while (await episodeSubtree.ReadAsync().ConfigureAwait(false))
                        {
                            if (episodeSubtree.NodeType == XmlNodeType.Element)
                            {
                                switch (episodeSubtree.Name)
                                {
                                    case "epno":
                                        //var epno = episodeSubtree.ReadElementContentAsString();
                                        //EpisodeInfo info = new EpisodeInfo();
                                        //info.AnimeSeriesIndex = series.AnimeSeriesIndex;
                                        //info.IndexNumberEnd = string(epno);
                                        //info.SeriesProviderIds.GetOrDefault(ProviderNames.AniDb);
                                        //episodes.Add(info);
                                        break;
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task ParseTags(Series series, XmlReader reader)
        {
            var genres = new List<GenreInfo>();

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "tag")
                {
                    if (!int.TryParse(reader.GetAttribute("weight"), out int weight))
                    {
                        weight = 0;
                    }

                    if (int.TryParse(reader.GetAttribute("id"), out int id) && IgnoredTagIds.Contains(id))
                    {
                        continue;
                    }

                    if (int.TryParse(reader.GetAttribute("parentid"), out int parentId)
                        && IgnoredTagIds.Contains(parentId))
                    {
                        continue;
                    }

                    using (var tagSubtree = reader.ReadSubtree())
                    {
                        while (await tagSubtree.ReadAsync().ConfigureAwait(false))
                        {
                            if (tagSubtree.NodeType == XmlNodeType.Element && tagSubtree.Name == "name")
                            {
                                var name = await tagSubtree.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (name == "18 restricted")
                                {
                                    series.OfficialRating = "XXX";
                                }
                                if (weight >= 400)
                                {
                                    genres.Add(new GenreInfo { Name = name, Weight = weight });
                                }
                            }
                        }
                    }
                }
            }

            series.Genres = genres.OrderBy(g => g.Weight).Select(g => g.Name).ToArray();
        }

        private async Task ParseResources(Series series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "resource")
                {
                    var type = reader.GetAttribute("type");
                    switch (type)
                    {
                        case "4":
                            while (reader.Read())
                            {
                                if (reader.NodeType == XmlNodeType.Element && reader.Name == "url")
                                {
                                    await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                    break;
                                }
                            }
                            break;
                    }
                }
            }
        }

        private string StripAniDbLinks(string text)
        {
            return AniDbUrlRegex.Replace(text, "${name}");
        }

        public static string ReplaceNewLine(string text)
        {
            return text.Replace("\n", "<br>");
        }

        private async Task ParseActors(MetadataResult<Series> series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "character")
                    {
                        using (var subtree = reader.ReadSubtree())
                        {
                            await ParseActor(series, subtree).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        private async Task ParseActor(MetadataResult<Series> series, XmlReader reader)
        {
            string name = null;
            string role = null;

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "name":
                            role = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            break;

                        case "seiyuu":
                            name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(role)) // && series.People.All(p => p.Name != name))
            {
                series.AddPerson(CreatePerson(
                    Plugin.Instance.Configuration.AniDbReplaceGraves ? name.Replace('`', '\'') : name,
                    PersonType.Actor, role));
            }
        }

        private void ParseRatings(Series series, XmlReader reader)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "permanent")
                    {
                        if (float.TryParse(
                            reader.ReadElementContentAsString(),
                            NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture,
                            out float rating))
                        {
                            series.CommunityRating = (float)Math.Round(rating, 1);
                        }
                    }
                }
            }
        }

        private async Task<(string, string)> ParseTitle(XmlReader reader, string preferredMetadataLangauge)
        {
            var titles = new List<Title>();

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "title")
                {
                    var language = reader.GetAttribute("xml:lang");
                    var type = reader.GetAttribute("type");
                    var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                    titles.Add(new Title
                    {
                        Language = language,
                        Type = type,
                        Name = name
                    });
                }
            }

            string title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLangauge).Name;
            string originalTitle = titles.Localize(Plugin.Instance.Configuration.OriginalTitlePreference, preferredMetadataLangauge).Name;

            return (title, originalTitle);
        }

        private async Task ParseCreators(MetadataResult<Series> series, XmlReader reader)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "name")
                {
                    var type = reader.GetAttribute("type");
                    var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                    if (type == "Animation Work")
                    {
                        series.Item.AddStudio(name);
                    }
                    else
                    {
                        series.AddPerson(CreatePerson(
                           Plugin.Instance.Configuration.AniDbReplaceGraves ? name.Replace('`', '\'') : name, type));
                    }
                }
            }
        }

        private PersonInfo CreatePerson(string name, string type, string role = null)
        {
            // todo find nationality of person and conditionally reverse name order

            if (!Enum.TryParse(type, out PersonKind personKind))
            {
                personKind = _typeMappings.GetValueOrDefault(type, PersonKind.Actor);
            }

            return new PersonInfo
            {
                Name = ReverseNameOrder(name),
                Type = personKind,
                Role = role
            };
        }

        public static string ReverseNameOrder(string name)
        {
            return name.Split(' ').Reverse().Aggregate(string.Empty, (n, part) => n + " " + part).Trim();
        }

        private static async Task DownloadSeriesData(string aid, string seriesDataPath, string cachePath, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(seriesDataPath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            DeleteXmlFiles(directory);

            var httpClient = Plugin.Instance.GetHttpClient();
            var url = string.Format(SeriesQueryUrl, ClientName, aid);

            await RequestLimiter.Tick().ConfigureAwait(false);
            await Task.Delay(Plugin.Instance.Configuration.AniDbRateLimit).ConfigureAwait(false);

            using (var response = await httpClient.GetAsync(url).ConfigureAwait(false))
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            using (var file = File.Open(seriesDataPath, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(file))
            {
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                text = text.Replace("&#x0;", "");

                var errorRegexMatch = _errorRegex.Match(text);
                if (errorRegexMatch.Success)
                {
                    throw new Exception("AniDB API error " + errorRegexMatch.Value);
                }

                await writer.WriteAsync(text).ConfigureAwait(false);
            }

            await ExtractEpisodes(directory, seriesDataPath).ConfigureAwait(false);
            await ExtractCast(cachePath, seriesDataPath).ConfigureAwait(false);
        }

        private static void DeleteXmlFiles(string path)
        {
            try
            {
                foreach (var file in new DirectoryInfo(path)
                    .EnumerateFiles("*.xml", SearchOption.AllDirectories))
                {
                    file.Delete();
                }
            }
            catch (DirectoryNotFoundException)
            {
                // No biggie
            }
        }

        private static async Task ExtractEpisodes(string seriesDataDirectory, string seriesDataPath)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, settings))
                {
                    await reader.MoveToContentAsync().ConfigureAwait(false);

                    // Loop through each element
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (reader.Name == "episode")
                            {
                                var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                                await SaveEpsiodeXml(seriesDataDirectory, outerXml).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }

        private static async Task ExtractCast(string cachePath, string seriesDataPath)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            var cast = new List<AniDbPersonInfo>();

            using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, settings))
                {
                    await reader.MoveToContentAsync().ConfigureAwait(false);

                    // Loop through each element
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "characters")
                        {
                            var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                            cast.AddRange(ParseCharacterList(outerXml));
                        }

                        if (reader.NodeType == XmlNodeType.Element && reader.Name == "creators")
                        {
                            var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                            cast.AddRange(ParseCreatorsList(outerXml));
                        }
                    }
                }
            }

            var serializer = new XmlSerializer(typeof(AniDbPersonInfo));
            foreach (var person in cast)
            {
                var path = GetCastPath(person.Name, cachePath);
                var directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);

                if (!File.Exists(path) || person.Image != null)
                {
                    try
                    {
                        using (var stream = File.Open(path, FileMode.Create))
                        {
                            serializer.Serialize(stream, person);
                        }
                    }
                    catch (IOException)
                    {
                        // ignore
                    }
                }
            }
        }

        public static AniDbPersonInfo GetPersonInfo(string cachePath, string name)
        {
            var path = GetCastPath(name, cachePath);
            var serializer = new XmlSerializer(typeof(AniDbPersonInfo));

            try
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        return serializer.Deserialize(stream) as AniDbPersonInfo;
                    }
                }
            }
            catch (IOException)
            {
                return null;
            }

            return null;
        }

        private static string GetCastPath(string name, string cachePath)
        {
            name = name.ToLowerInvariant();
            return Path.Combine(cachePath, "anidb-people", name[0].ToString(), name + ".xml");
        }

        private static IEnumerable<AniDbPersonInfo> ParseCharacterList(string xml)
        {
            var doc = XDocument.Parse(xml);
            var people = new List<AniDbPersonInfo>();

            var characters = doc.Element("characters");
            if (characters != null)
            {
                foreach (var character in characters.Descendants("character"))
                {
                    var seiyuu = character.Element("seiyuu");
                    if (seiyuu != null)
                    {
                        var person = new AniDbPersonInfo
                        {
                            Name = ReverseNameOrder(seiyuu.Value)
                        };

                        var picture = seiyuu.Attribute("picture");
                        if (picture != null && !string.IsNullOrEmpty(picture.Value))
                        {
                            person.Image = "https://cdn.anidb.net/images/main/" + picture.Value;
                        }

                        var id = seiyuu.Attribute("id");
                        if (id != null && !string.IsNullOrEmpty(id.Value))
                        {
                            person.Id = id.Value;
                        }

                        people.Add(person);
                    }
                }
            }

            return people;
        }

        private static IEnumerable<AniDbPersonInfo> ParseCreatorsList(string xml)
        {
            var doc = XDocument.Parse(xml);
            var people = new List<AniDbPersonInfo>();

            var creators = doc.Element("creators");
            if (creators != null)
            {
                foreach (var creator in creators.Descendants("name"))
                {
                    var type = creator.Attribute("type");
                    if (type != null && type.Value == "Animation Work")
                    {
                        continue;
                    }

                    var person = new AniDbPersonInfo
                    {
                        Name = ReverseNameOrder(creator.Value)
                    };

                    var id = creator.Attribute("id");
                    if (id != null && !string.IsNullOrEmpty(id.Value))
                    {
                        person.Id = id.Value;
                    }

                    people.Add(person);
                }
            }

            return people;
        }

        private static async Task SaveXml(string xml, string filename)
        {
            var writerSettings = new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                Async = true
            };

            using (var writer = XmlWriter.Create(filename, writerSettings))
            {
                await writer.WriteRawAsync(xml).ConfigureAwait(false);
            }
        }

        private static async Task SaveEpsiodeXml(string seriesDataDirectory, string xml)
        {
            var episodeNumber = await ParseEpisodeNumber(xml).ConfigureAwait(false);

            if (episodeNumber != null)
            {
                var file = Path.Combine(seriesDataDirectory, string.Format("episode-{0}.xml", episodeNumber));
                await SaveXml(xml, file);
            }
        }

        private static async Task<string> ParseEpisodeNumber(string xml)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                ValidationType = ValidationType.None
            };

            using (var streamReader = new StringReader(xml))
            {
                // Use XmlReader for best performance
                using (var reader = XmlReader.Create(streamReader, settings))
                {
                    reader.MoveToContent();

                    // Loop through each element
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (reader.Name == "epno")
                            {
                                var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(val))
                                {
                                    return val;
                                }
                            }
                            else
                            {
                                await reader.SkipAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the series data path.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="seriesId">The series id.</param>
        /// <returns>System.String.</returns>
        public static string GetSeriesDataPath(IApplicationPaths appPaths, string seriesId)
        {
            return Path.Combine(appPaths.CachePath, "anidb", "series", seriesId);
        }

        private struct GenreInfo
        {
            public string Name;
            public int Weight;
        }
    }

    public class Title
    {
        public string Language { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
    }

    public static class TitleExtensions
    {
        public static Title Localize(this IEnumerable<Title> titles, TitlePreferenceType preference, string metadataLanguage)
        {
            var titlesList = titles as IList<Title> ?? titles.ToList();

            if (preference == TitlePreferenceType.Localized)
            {
                // prefer an official title, else look for a synonym
                var localized = titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "main") ??
                                titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "official") ??
                                titlesList.FirstOrDefault(t => t.Language == metadataLanguage && t.Type == "synonym");

                if (localized != null)
                {
                    return localized;
                }
            }

            if (preference == TitlePreferenceType.Japanese)
            {
                // prefer an official title, else look for a synonym
                var japanese = titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "main") ??
                               titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "official") ??
                               titlesList.FirstOrDefault(t => t.Language == "ja" && t.Type == "synonym");

                if (japanese != null)
                {
                    return japanese;
                }
            }

            // return the main title (romaji)
            return titlesList.FirstOrDefault(t => t.Language == "x-jat" && t.Type == "main") ??
                   titlesList.FirstOrDefault(t => t.Type == "main") ??
                   titlesList.FirstOrDefault();
        }
    }
}
