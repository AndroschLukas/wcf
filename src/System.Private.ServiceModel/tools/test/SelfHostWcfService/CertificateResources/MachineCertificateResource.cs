﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WcfTestBridgeCommon;

namespace WcfService.CertificateResources
{
    internal class MachineCertificateResource : CertificateResource
    {
        public MachineCertificateResource() : base() { }
        
        public override ResourceResponse Get(ResourceRequestContext context)
        {
            string thumbprint;
            bool thumbprintPresent = context.Properties.TryGetValue(thumbprintKeyName, out thumbprint) && !string.IsNullOrWhiteSpace(thumbprint);

            string subject;
            bool subjectPresent = context.Properties.TryGetValue(subjectKeyName, out subject) && !string.IsNullOrWhiteSpace(subject);

            ResourceResponse response = new ResourceResponse();

            // if no subject and no thumbprint parameter provided, provide a list of certs already PUT to this resource 
            if (!thumbprintPresent && !subjectPresent)
            {
                string retVal = string.Empty;
                string[] subjects;
                string[] thumbprints;

                lock (s_certificateResourceLock)
                {
                    int certNum = s_createdCertsBySubject.Count;
                    subjects = new string[certNum];
                    thumbprints = new string[certNum];

                    foreach (var keyVal in s_createdCertsBySubject)
                    {
                        --certNum;
                        subjects[certNum] = keyVal.Key;
                        thumbprints[certNum] = keyVal.Value.Thumbprint;
                    }
                }

                response.Properties.Add(subjectsKeyName, string.Join(",", subjects));
                response.Properties.Add(thumbprintsKeyName, string.Join(",", thumbprints));
                return response;
            }
            else
            {
                // Otherwise, check on the creation state given the certificate thumbprint or subject
                // thumbprint is given priority if present

                X509Certificate2 certificate = null;
                bool certHasBeenCreated = false;

                lock (s_certificateResourceLock)
                {
                    if (thumbprintPresent)
                    {
                        certHasBeenCreated = s_createdCertsByThumbprint.TryGetValue(thumbprint, out certificate);
                    }
                    else if (subjectPresent)
                    {
                        certHasBeenCreated = s_createdCertsBySubject.TryGetValue(subject, out certificate);
                    }
                }

                if (certHasBeenCreated)
                {
                    response.Properties.Add(thumbprintKeyName, certificate.Thumbprint);
                    response.Properties.Add(certificateKeyName, Convert.ToBase64String(certificate.RawData));
                }
                else
                {
                    response.Properties.Add(thumbprintKeyName, string.Empty);
                    response.Properties.Add(certificateKeyName, string.Empty);
                }
                return response;
            }
        }

        // Requests a certificate to be generated by the Bridge
        // If the certificate requested is for the local machine, for example if 
        // server hostname is: foo.bar.com
        // local address is considered to be: 127.0.0.1, localhost, foo, foo.bar.com
        // Then we also install the certificate to the local machine, because it means we are about to run an HTTPS/SSL test against 
        // this machine. 
        // Otherwise, don't bother installing as the cert is for a remote machine. 
        public override ResourceResponse Put(ResourceRequestContext context)
        {
            X509Certificate2 certificate;

            string subject; 
            if (!context.Properties.TryGetValue(subjectKeyName, out subject) || string.IsNullOrWhiteSpace(subject))
            {
                throw new ArgumentException("When PUTting to this resource, specify an non-empty 'subject'", "context.Properties");
            }

            // There can be multiple subjects, separated by ,
            string[] subjects = subject.Split(',');

            bool isLocal = IsLocalMachineResource(subjects[0]);

            lock (s_certificateResourceLock)
            {
                if (!s_createdCertsBySubject.TryGetValue(subjects[0], out certificate))
                {
                    CertificateGenerator generator = CertificateResourceHelpers.GetCertificateGeneratorInstance(context.BridgeConfiguration);

                    if (isLocal)
                    {
                        // If we're PUTting a cert that refers to a hostname local to the bridge, 
                        // return the Local Machine cert that CertificateManager caches and add it to the collection
                        //
                        // If we are receiving a PUT to the same endpoint address as the bridge server, it means that 
                        // a test is going to be run on this box
                        //
                        // In keeping with the semantic of these classes, we must PUT before we can GET a cert
                        certificate = CertificateManager.CreateAndInstallLocalMachineCertificates(generator);
                    }
                    else
                    {
                        certificate = generator.CreateCertificate(subjects).Certificate;
                    }
                    // Cache the certificates
                    s_createdCertsBySubject.Add(subjects[0], certificate);
                    s_createdCertsByThumbprint.Add(certificate.Thumbprint, certificate);
                }
            }

            ResourceResponse response = new ResourceResponse();
            response.Properties.Add(thumbprintKeyName, certificate.Thumbprint);
            response.Properties.Add(isLocalKeyName, isLocal.ToString());

            return response;
        }
    }
}