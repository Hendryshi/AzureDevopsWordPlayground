﻿using HtmlAgilityPack;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Proxy;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WordExporter.Core.Support;
using WordExporter.Core.WordManipulation.Support;

namespace WordExporter.Core.WorkItems
{
    public static class Extensions
    {
        /// <summary>
        /// Simply create a dictionary of substitution values from all the fields
        /// of the work item.
        /// </summary>
        /// <param name="workItem"></param>
        /// <returns></returns>
        public static Dictionary<String, Object> CreateDictionaryFromWorkItem(
            this WorkItem workItem)
        {
            Log.Logger.Debug("Creating dictionary information for work item {id}", workItem.Id);
            var retValue = new Dictionary<String, Object>(StringComparer.OrdinalIgnoreCase);
            workItem.FillPropertyDictionary(retValue, "", false);
            return retValue;
        }

        /// <summary>
        /// From a <see cref="WorkItem"/> creates a dictionary with the name of the field as key and
        /// value of the field as value. Some special fields, like description, are converted to html
        /// images are downloaded and embedded (if requested).
        /// </summary>
        /// <param name="workItem"></param>
        /// <param name="dictionary"></param>
        /// <param name="prefix"></param>
        /// <param name="skipHtmlFields">If true, the caller does not want to retrieve HTML field
        /// with full content, like images embedded. It is true when we are creating dictionary for
        /// excel, since we cannot embed images in excel.</param>
        public static void FillPropertyDictionary(
            this WorkItem workItem,
            Dictionary<string, object> dictionary,
            String prefix,
            Boolean skipHtmlFields)
        {
            Log.Logger.Debug("Filling dictionary information for work item {id}", workItem.Id);
            var tempDictionary = new Dictionary<String, Object>();
            tempDictionary["title"] = workItem.Title;
            tempDictionary["id"] = workItem.Id;
            //Some help to avoid forcing the user to use System.AssignedTo etc for most commonly used fields.
            if (skipHtmlFields)
            {
                tempDictionary["description"] = GetTxtFromHtmlContent(workItem.Description);
            }
            else
            {
                tempDictionary["description"] = new HtmlSubstitution(workItem.GenerateHtmlForWordEmbedding(workItem.Description, Registry.Options.NormalizeFontInDescription));
            }
            tempDictionary["description.txt"] = GetTxtFromHtmlContent(workItem.Description);
            tempDictionary["assignedto"] = workItem.Fields["System.AssignedTo"].Value?.ToString() ?? String.Empty;
            tempDictionary["createdby"] = workItem.Fields["System.CreatedBy"].Value?.ToString() ?? String.Empty;

            HashSet<String> specialFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "description",
            };

            //All the fields will be set in raw format.
            foreach (Field field in workItem.Fields)
            {
                if (specialFields.Contains(field.Name))
                    continue; //This is a special field, ignore.

                tempDictionary[field.Name] = tempDictionary[field.ReferenceName] = GetValue(field);
            }

            var comments = ConnectionManager.Instance
                .WorkItemTrackingHttpClient
                .GetCommentsAsync(workItem.Id).Result;

            if (comments.Count > 0)
            {
                StringBuilder htmlComment = new StringBuilder();
                foreach (var comment in comments.Comments)
                {
                    htmlComment.Append($"<b>Author:</b>{comment.RevisedBy.DisplayName} in date {comment.RevisedDate.ToString("yyyy/MM/dd hh:mm")}");
                    htmlComment.Append("<br>");
                    htmlComment.Append(comment.Text);
                }

                if (skipHtmlFields)
                {
                    tempDictionary["comments"] = GetTxtFromHtmlContent(htmlComment.ToString());
                }
                else
                {
                    var commentsInHtml = workItem.GenerateHtmlForWordEmbedding(htmlComment.ToString(), Registry.Options.NormalizeFontInDescription);
                    tempDictionary["comments"] = new HtmlSubstitution(commentsInHtml);
                }
            }
            else
            {
                tempDictionary["comments"] = "";
            }

            //ok some of the historical value could be of interests, as an example the last user timestamp for each state change
            //is an information that can be interesting
            if (workItem.Revisions.Count > 0)
            {
                foreach (Revision revision in workItem.Revisions)
                {
                    var fieldsChanged = revision
                        .Fields
                        .OfType<Field>()
                        .Where(f => f.IsChangedInRevision)
                        .ToList();
                    var changedBy = revision.Fields["Changed By"].Value;
                    var changedDate = revision.Fields["Changed Date"].Value;
                    foreach (var field in fieldsChanged)
                    {
                        if (field.ReferenceName.Equals("system.state", StringComparison.OrdinalIgnoreCase))
                        {
                            tempDictionary[$"statechange.{field.Value.ToString().ToLower()}.author"] = changedBy;
                            tempDictionary[$"statechange.{field.Value.ToString().ToLower()}.date"] = ((DateTime)changedDate).ToShortDateString();
                        }
                        else if (field.ReferenceName.Equals("system.areapath", StringComparison.OrdinalIgnoreCase))
                        {
                            tempDictionary["lastareapathchange.author"] = changedBy;
                            tempDictionary["lastareapathchange.date"] = ((DateTime)changedDate).ToShortDateString();
                        }
                    }
                }
            }

            foreach (var element in tempDictionary)
            {
                dictionary[$"{prefix}{element.Key}"] = element.Value;
            }
        }

        public static Object GetValue(this Field field)
        {
            if (field.Value == null)
                return String.Empty;

            if (field.Value is DateTime dateTime)
                return dateTime.ToShortDateString();

            if (field.FieldDefinition.FieldType == FieldType.Html)
            {
                return new HtmlSubstitution(field.WorkItem.GenerateHtmlForWordEmbedding(field.Value.ToString(), Registry.Options.NormalizeFontInDescription));
            }

            return field.Value.ToString();
        }

        /// <summary>
        /// This is not an extension method because it does not use WorkItem
        /// </summary>
        /// <param name="workItem"></param>
        /// <param name="htmlContent"></param>
        /// <returns></returns>
        public static String GetTxtFromHtmlContent(String htmlContent)
        {
            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);
            return doc.DocumentNode.InnerText;
        }

        public static String GenerateHtmlForWordEmbedding(
            this WorkItem workItem,
            String htmlContent,
            Boolean normalizeFont)
        {
            try
            {
                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(htmlContent);

                var images = doc.DocumentNode.SelectNodes("//img");
                if (images != null)
                {
                    foreach (var image in images)
                    {
                        try
                        {
                            DownloadAndEmbedImage(workItem, image);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Unable to embed image {img}", image.OuterHtml);
                        }
                    }
                }

                if (normalizeFont)
                {
                    RemoveAttributeFromDocument(doc, "style");
                    doc.RemoveTags(doc.CreateElement("br"), "p");
                }
                return doc.DocumentNode.OuterHtml;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unable to generate embeddable html: {htmlContent}", htmlContent);
                return "Error converting HTML text: " + ex.Message;
            }
        }

        private static void DownloadAndEmbedImage(WorkItem workItem, HtmlNode image)
        {
            var src = image.GetAttributeValue("src", "");
            try
            {
                //need to understand if it is in base 64 or no, if the answer is no, we need to embed image
                if (!String.IsNullOrEmpty(src))
                {
                    if (src.Contains("base64")) // data:image/jpeg;base64,
                    {
                        //image already embedded
                        Log.Debug("found image in html content that was already in base64");
                    }
                    else
                    {
                        Log.Debug("found image in html content that point to external image {src}", src);
                        String downloadedAttachment = "";
                        String extension = "";
                        //is it a internal attached images?
                        var match = Regex.Match(src, @"FileID=(?<id>\d*)", RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            var attachment = workItem.Attachments
                                .OfType<Attachment>()
                                .FirstOrDefault(_ => _.Id.ToString() == match.Groups["id"].Value);
                            if (attachment != null)
                            {
                                //ok we can embed in the image as base64
                                WorkItemServer wise = workItem.Store.TeamProjectCollection.GetService<WorkItemServer>();
                                downloadedAttachment = wise.DownloadFile(attachment.Id);
                                extension = attachment.Extension.Trim('.');
                            }
                        }
                        else if ((match = GetUrlMatches(src)).Success)
                        {
                            var serverCredentials = ConnectionManager.Instance.GetCredentials();
                            string fileName = match.Groups["fileName"].Value;
                            extension = Path.GetExtension(fileName).Trim('.');
                            downloadedAttachment = Path.GetTempFileName() + "." + extension;
                            
                            if (serverCredentials != null)
                            {
                                using (var client = new WebClient())
                                {
                                    client.Credentials = serverCredentials;
                                    client.DownloadFile(src, downloadedAttachment);
                                }
                            }
                            else
                            {
                                //download with standard client
                                var fileId = match.Groups["fileId"].Value;
                                using (var fs = new FileStream(downloadedAttachment, FileMode.Create))
                                {
                                    var downloadStream = ConnectionManager.Instance
                                        .WorkItemTrackingHttpClient
                                        .GetAttachmentContentAsync(new Guid(fileId), fileName, download: true).Result;
                                    using (downloadStream)
                                    {
                                        downloadStream.CopyTo(fs);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Log.Error("Unable to embed image with url {src}", src);
                        }

                        if (!String.IsNullOrEmpty(downloadedAttachment))
                        {
                            byte[] byteContent = File.ReadAllBytes(downloadedAttachment);
                            String base64Encoded = Convert.ToBase64String(byteContent);
                            var newSrcValue = $"data:image/{extension};base64,{base64Encoded}";
                            image.SetAttributeValue("src", newSrcValue);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Unable to download and embed image with url {src}", src);
            }
        }

        private static Match GetUrlMatches(String input)
        {
            var match = Regex.Match(input, @"_apis/wit/attachments/(?<fileId>.*)\?fileName=(?<fileName>.*)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match;
            }

            //We can have different format for image url, if the first one failed, we can try another
            return Regex.Match(input, @"FileNameGuid=(?<fileId>.*).*fileName=(?<fileName>.*)", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Given an HTML tag that is not permitted as embed in word document,
        /// we can simply collapse the tag with plain text content
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="nodeName"></param>
        private static void CollapseNodeWithText(HtmlDocument doc, string nodeName)
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{nodeName}");
            if (nodes != null)
            {
                foreach (var node in nodes.ToList())
                {
                    node.ParentNode.ReplaceChild(HtmlNode.CreateNode(node.InnerText), node);
                }
            }
        }

        private static void RemoveAttributeFromDocument(HtmlDocument doc, string classn)
        {
            var classAttribute = doc.DocumentNode.SelectNodes($"//*[@{classn}]");
            if (classAttribute != null)
            {
                foreach (var nodeWithClassAttribute in classAttribute)
                {
                    nodeWithClassAttribute.Attributes[classn].Remove();
                }
            }
        }
    }
}