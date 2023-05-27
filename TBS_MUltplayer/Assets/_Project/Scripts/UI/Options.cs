using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
public class Options : MonoBehaviour
{
    public AudioMixer audioMixer;
   public void SetAudio(float volume)
   {
        audioMixer.SetFloat("volume", volume);
   }
    public void SetQuailty(int quailty_index)
    {
        QualitySettings.SetQualityLevel(quailty_index);
    }

}
