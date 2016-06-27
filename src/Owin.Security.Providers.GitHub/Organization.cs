using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Owin.Security.Providers.GitHub
{
    public class Organization
    {
        public string Login { get; set; }
        public int Id { get; set; }
        public string Url { get; set; }
        public string ReposUrl { get; set; }
        public string EventsUrl { get; set; }
        public string HooksUrl { get; set; }
        public string IssuesUrl { get; set; }
        public string MembersUrl { get; set; }
        public string PulblicMembersUrl { get; set; }
        public string AvatarUrl { get; set; }
        public string Description { get; set; }

    }
}
