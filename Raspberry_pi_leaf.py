#!/usr/bin/env python

import MySQLdb
import datetime, time
from datetime import timedelta
import pygame
import sched
import os
import sys
import termios
import tty
import pigpio
import numpy
import math
import RPi.GPIO as GPIO


# Open database connection
db = MySQLdb.connect("localhost", "User", "password", "emotions")
curs = db.cursor()

# create a cursor for the select
arrayEmotion = []
arrayTime = []
avgEmo = 0
pi = pigpio.pi()
GPIO.setmode(GPIO.BCM)
GPIO.setwarnings(False)
GPIO.setup(3,GPIO.OUT)

    
def updateDatabase():
    db = MySQLdb.connect("localhost", "User", "password", "emotions")
    updatedCurs = db.cursor()
    return updatedCurs

def printTableTop():
    print("ID Time                 emotion")
    print("=================================")

def readLastEmotions(lastID, amount = 12):
    print("+++++++++READ LAST EMOTIIONS!")
    curs.execute ("SELECT * FROM emotiondata WHERE ID  = %s ORDER BY time DESC LIMIT %s",(lastID,amount,) )
    printTableTop()
    
    arrayEmotion = []
    global arrayTime 
    arrayTime = []
    for row in curs.fetchall():
        arrayTime.append(row[1])
        arrayEmotion.append(row[2])
        print(str(row[0]) + "  " + str(row[1]) + "  " + str(row[2]))
    return arrayEmotion

def areLastEmotionsNew(_lastTime, amount = 6):
    tempArrayTime = []
    curs.execute ("SELECT time FROM emotiondata WHERE time > %s ORDER BY time DESC", (_lastTime,))
    for row in curs.fetchall():
        tempArrayTime.append(row)
    if len(tempArrayTime) >= amount:
        _lastTime = tempArrayTime[0]
        print("lastTime in areLastEmotionsNew: " + str (_lastTime))
        return True, _lastTime
    else:
        return False, None

def getLastID():
    # check if ID is still the same
    query = "SELECT ID FROM emotiondata ORDER BY time DESC LIMIT 1"
    curs.execute (query) 
    currentID = curs.fetchall()[0][0] # 0th Index
##    print("LAST ID: " + str(currentID))
    return currentID

def reset():
    print("RESET")
    
    # Reset sound
    ambientVolume = ambientChannel.set_volume(1)
    highPitchVolume = highPitchChannel.set_volume(0)
    noiseVolume = noiseChannel.set_volume(0)
    
    GPIO.output(3,GPIO.HIGH)
    print("++++++VENTILATOR ON")
    setLights(RED_PIN,255)
    setLights(BLUE_PIN, 10)
    setLights(GREEN_PIN, 60)
    
    

def calcMean(arrayEmotion):
    global avgEmo
    # Errechnet den avg der ersten Minute und steckt diese in eine Variable
    avgEmo = numpy.mean(arrayEmotion)
    # Geschwindigkeitswert, der an die LEDs und Soundboxen weitergegeben wird
    print("avgEmo: " + str(avgEmo))
    

############### SOUND function
pygame.mixer.init(frequency=22050,size=-16,channels=4) #frequency, size, channels, buffersize
background = pygame.mixer.Sound('Music/lofi_gesamt.wav')



flyNose = pygame.mixer.Sound('Music/fly.wav')
highPitch = pygame.mixer.Sound('Music/high-pitch-large.wav')

# create separate Channel objects for simultaneous playback
ambientChannel = pygame.mixer.Channel(0) # argument must be int
noiseChannel = pygame.mixer.Channel(1)
highPitchChannel = pygame.mixer.Channel(2)

def increaseVolume(step, channel):
    volume_channel = channel.get_volume()
    if volume_channel < 1:
        volume_channel += step
        channel.set_volume(volume_channel)
    print(volume_channel)
    
def decreaseVolume(step, channel):
    volume_channel = channel.get_volume()
    if volume_channel > 0 :
        volume_channel -= step
        channel.set_volume(volume_channel)
    else :
        channel.set_volume(0)
    print(volume_channel)
    

def soundMixer(avgEmo):
    # Mix sound according to avgEmo
    if avgEmo > 0.55:
        print("FAST Sound steps: 0.1")
        newSteps = 0.06
    elif avgEmo < 0.45:
        print("SLOW Sound steps: 0.01")
        newSteps = 0.01
    elif avgEmo <= 0.55 and avgEmo >= 0.45:
        print("Sound steps: 0.05")
        newSteps = 0.03
        
    decreaseVolume(newSteps, ambientChannel)
    increaseVolume(newSteps, highPitchChannel)
    increaseVolume(newSteps, noiseChannel)
    print("HighPitchChannel Volume: " + str(highPitchChannel.get_volume()))
    
def setVentilator(avgEmo, _timeStamp):
    if avgEmo > 0.55:
        tempTimer = _timeStamp + timedelta(seconds = 20)
        if datetime.datetime.now() > tempTimer:
            GPIO.output(3,GPIO.LOW)
            return (False, None, 0)
                
    elif avgEmo < 0.45:
        tempTimer = _timeStamp + timedelta(seconds = 60)
        if datetime.datetime.now() > tempTimer:
            GPIO.output(3,GPIO.LOW)
            return (False, None, 0)
            
    elif avgEmo <= 0.55 and avgEmo >= 0.45:
        tempTimer = _timeStamp + timedelta(seconds = 40)
        if datetime.datetime.now() > tempTimer:
            GPIO.output(3,GPIO.LOW)
            return (False, None, 0)
    return (True, _timeStamp, 3)

    
    
############## LED CONTROL
    
RED_PIN   = 17
GREEN_PIN = 22
BLUE_PIN  = 24

# Number of color changes per step (more is faster, less is slower).
# You also can use 0.X floats.


def setLights(pin, brightness):
    bright = 255
    realBrightness = int(int(brightness) * (float(bright) / 255.0))
    pi.set_PWM_dutycycle(pin, realBrightness)

def getLightness(pin):
    return pi.get_PWM_dutycycle(pin)

# 
def coldLoop(avgEmo):
    
    factor = float(1.0/112.5)
    # if whileCounter %8
    #Change avgEmo to 5 min
    if avgEmo > 0.55:
        factor = float(1.0/37.5)
    #Change avgEmo tp 15 min
    elif avgEmo <= 0.55 and avgEmo >= 0.45:
        factor = float(1.0/112.5)
    #Change avgEmo tp 20 min
    elif avgEmo < 0.45:
        factor = float(1.0/150.0)
        
    if int(getLightness(BLUE_PIN)) <= 250 :
        g = round(getLightness(GREEN_PIN) + factor*120.0)
        b = round(getLightness(BLUE_PIN) + factor*245.0)
        r = getLightness(RED_PIN) + factor*(-60.0)
        setLights(GREEN_PIN, g)
        setLights(BLUE_PIN,b)
        setLights(RED_PIN,r)
        print("GREEN PIN: " + str(getLightness(GREEN_PIN)))
        print("BLUE_PIN: " + str(getLightness(BLUE_PIN)))
        print("RED_PIN: " + str(getLightness(RED_PIN)))
    elif int(getLightness(BLUE_PIN)) > 250 :
        setLights(GREEN_PIN, 180)
        setLights(BLUE_PIN,255)
        setLights(RED_PIN,200)
            
        
def warmLoop():
        if int(getLightness(BLUE_PIN)) > 10 :
            j = int(getLightness(BLUE_PIN)) + 10
            setLights(BLUE_PIN, j);
            setLights(GREEN_PIN, 60 + j)
            print("SET Current LED RED Brightness = " + str(int(getLightness(RED_PIN))))
            
def isHumanAnwesend():
    lastSeconds = datetime.datetime.now() - timedelta(seconds = 30)
    curs.execute("SELECT time FROM emotiondata WHERE time > %s ORDER BY time DESC", (lastSeconds,))
    timeArray = curs.fetchall()
    if timeArray == ():
        return False
    else:
        return True

# Define the main function
def main():
    lastTime = None
    lastID = 0
    whileCounter = 0
    reset()
    ambientChannel.play(background,-1)
    noiseChannel.play(flyNose, -1)
    highPitchChannel.play(highPitch,-1)
    emoCounter = 0
    timerActive = False
    timeStamp = None
    avgEmo2 = None
    tempEmo = []
    
    while True:
        whileCounter += 2
        # You need to update the Database to get the most recent data
        
        global curs
        curs = updateDatabase()
        curs.execute("SELECT * FROM emotiondata;")
        
        if curs.fetchall() != ():
            if lastID != getLastID():
                lastTime = None
                reset()
                
                
            lastID = getLastID()
            
            print("Is someone in the room? " + str(isHumanAnwesend()))
            
            #A human is anwesend
            
            if isHumanAnwesend():
                # run LED change
                if whileCounter % 10 == 0:
                    coldLoop(avgEmo)
                # run sound mixing
                if whileCounter % 30 == 0:
                    soundMixer(avgEmo)
                
                # Set timer for ventilator if 4 Minutes passed
                if timerActive == True:
                    print("+++++timeStamp: " + str(timeStamp))
                    print("+++++timerActive: " + str(timerActive))
                    timerActive, timeStamp, emoCounter = setVentilator(avgEmo2, timeStamp)
                
                # Save last 12 emotions for the first time
                if lastTime == None:
                    lastEmos = readLastEmotions(lastID, 12)
                    if len(lastEmos) >= 12:
                        calcMean(readLastEmotions(lastID, 12))
                        lastTime = arrayTime[0]
                        
                # If this is not the first saving of emotions
                else:
                    #Are the last 12 entries new?
                    emotionsNew, tempLastTime = areLastEmotionsNew(lastTime, 12)
                    if emotionsNew:
                        emoCounter += 1
                        print("+++++emoCounter: " + str(emoCounter))
                        lastTime = tempLastTime
                        print("New entries!+")
                        calcMean(readLastEmotions(lastID, 12))
                        if emoCounter >= 2:
                            # Set timer for Ventilator if 4 Minutes passed
                            if timerActive == False and GPIO.input(3) == 1:
                                curs.execute ("SELECT * FROM emotiondata WHERE ID  = %s ORDER BY time DESC LIMIT 24",(lastID,) )
                                tempEmo = []
                                for row in curs.fetchall():
                                    tempEmo.append(row[2])
                                avgEmo2 = numpy.mean(tempEmo)
                                timeStamp = datetime.datetime.now()
                                timerActive = True 
                    else:
                        print("No new entries")
            else:
                # Reset everything
                reset()
                timeStamp = None
                timerActive = False
                emoCounter = 0
                avgEmo2 = None
                tempEmo = []
                print("+++++emoCounter: " + str(emoCounter))
                
            time.sleep(2)
        else:
            print("Database empty")
            time.sleep(2)

main()
